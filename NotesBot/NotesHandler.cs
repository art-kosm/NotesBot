using Telegram.Bot.Types;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace NotesBot
{
    internal class NotesHandler
    {
        struct Note
        {
            public NoteText noteText;
            public DateTime createdTime;
        }

        struct NoteText
        {
            public string text;
            public string category = string.Empty;

            public NoteText(string text)
            {
                this.text = text;
                if (text.StartsWith('.'))
                {
                    int index = text.IndexOf('\n');
                    if (index != -1)
                    {
                        category = text[1..index];
                        this.text = text[(index + 1)..];
                    }
                }
            }
        }

        const string NotesFilePath = "notes.yml";
        readonly ChatId m_chatId = Convert.ToInt32(Environment.GetEnvironmentVariable("CHAT_ID"));
        readonly string m_botUsername = $"@{Environment.GetEnvironmentVariable("BOT_USERNAME")}";

        readonly TelegramBotClient m_botClient;
        readonly Dictionary<int, Note> m_notesMap = new(); // note id -> note
        readonly Dictionary<int, int> m_noteToMessageMap = new(); // note id -> message id
        int m_currentNoteId = 0;
        bool m_showCreatedTime = false;
        bool m_resended = false;

        public NotesHandler(TelegramBotClient botClient, CancellationTokenSource cts)
        {
            m_botClient = botClient;

            System.Timers.Timer timer = new(30 * 1000);
            timer.Elapsed += async (sender, e) => await ResendMessages(cts);
            timer.Start();
        }

        async Task ResendMessages(CancellationTokenSource cts)
        {
            int hour = DateTime.Now.Hour;
            if (hour >= 7)
            {
                m_resended = false;
            }

            if (hour == 6 &&
                !m_resended)
            {
                foreach (KeyValuePair<int, Note> pair in m_notesMap)
                {
                    await UpdateNote(
                            noteText: pair.Value.noteText,
                            noteId: pair.Key,
                            cancellationToken: cts.Token);
                }

                m_resended = true;
            }
        }

        public async Task LoadSavedNotes(CancellationToken cancellationToken)
        {
            using StreamReader reader = new(NotesFilePath);
            string s = reader.ReadToEnd();

            Dictionary<int, Note> deserializedNotesMap = GetDeserializer()
                                                         .Deserialize<Dictionary<int, Note>>(s);
            foreach (KeyValuePair<int, Note> node in deserializedNotesMap)
            {
                await AddNewNote(node.Value,
                                 cancellationToken);
            }
        }

        public async Task ProcessMessage(Message message, CancellationToken cancellationToken)
        {
            if (message.Chat.Id != m_chatId)
                return;

            // Only process text messages
            if (message.Text is not { } messageText)
                return;

            string editHead = m_botUsername + " /edit ";
            string deleteHead = m_botUsername + " /delete ";
            string tagHead = m_botUsername + " /tag ";
            const string showCreatedTimeHead = "/show_created_time ";
            if (messageText.StartsWith(editHead))
            {
                string commandString = messageText[editHead.Length..];
                int index = commandString.IndexOf(' ');
                if (index != -1)
                {
                    int noteId = Convert.ToInt32(commandString[..index]);
                    if (m_notesMap.ContainsKey(noteId))
                    {
                        string editText = commandString[(index + 1)..];
                        await UpdateNote(
                            noteText: new NoteText(editText),
                            noteId: noteId,
                            cancellationToken: cancellationToken);
                    }
                }
            }
            else if (messageText.StartsWith(tagHead))
            {
                string commandString = messageText[tagHead.Length..];
                int index = commandString.IndexOf(' ');
                if (index != -1)
                {
                    int noteId = Convert.ToInt32(commandString[..index]);
                    if (m_notesMap.ContainsKey(noteId))
                    {
                        string category = commandString[(index + 1)..];
                        await UpdateNote(
                            noteText: new NoteText
                            {
                                text = m_notesMap[noteId].noteText.text,
                                category = category
                            },
                            noteId: noteId,
                            cancellationToken: cancellationToken);
                    }
                }
            }
            else if (messageText.StartsWith(deleteHead))
            {
                int noteId = Convert.ToInt32(messageText[deleteHead.Length..]);
                if (m_notesMap.ContainsKey(noteId))
                {
                    LogDeletedNote(m_notesMap[noteId]);
                    m_notesMap.Remove(noteId);

                    await DeleteMessage(
                        messageId: m_noteToMessageMap[noteId],
                        cancellationToken: cancellationToken);

                    m_noteToMessageMap.Remove(noteId);
                }
            }
            else if (messageText.StartsWith(showCreatedTimeHead))
            {
                m_showCreatedTime = Convert.ToInt32(messageText[showCreatedTimeHead.Length..]) != 0;
            }
            else
            {
                await AddNewNote(new()
                                 {
                                     noteText = new(messageText),
                                     createdTime = DateTime.Now
                                 },
                                 cancellationToken);
            }

            await DeleteMessage(
                messageId: message.MessageId,
                cancellationToken: cancellationToken);
            LogToFile(message.Chat.Username, messageText);
            WriteYaml(m_notesMap, NotesFilePath);
        }

        async Task UpdateNote(int noteId, NoteText noteText, CancellationToken cancellationToken)
        {
            Note note = new()
            {
                noteText = noteText,
                createdTime = m_notesMap[noteId].createdTime
            };

            m_notesMap[noteId] = note;
            await DeleteMessage(
                messageId: m_noteToMessageMap[noteId],
                cancellationToken: cancellationToken);
            await SendNewNoteMessage(
                note: note,
                noteId: noteId,
                cancellationToken: cancellationToken);
        }

        async Task SendNewNoteMessage(Note note, int noteId, CancellationToken cancellationToken)
        {
            Message sentMessage = await SendMessage(
                    note: note,
                    noteId: noteId,
                    cancellationToken: cancellationToken);

            UpdateNoteMessageId(noteId, sentMessage.MessageId);
        }

        async Task AddNewNote(Note note, CancellationToken cancellationToken)
        {
            int noteId = m_currentNoteId;
            m_currentNoteId++;
            m_notesMap.Add(noteId, note);

            await SendNewNoteMessage(
                note: note,
                noteId: noteId,
                cancellationToken: cancellationToken);
        }

        void UpdateNoteMessageId(int noteId, int messageId)
        {
            m_noteToMessageMap[noteId] = messageId;
        }

        async Task<Message> SendMessage(Note note, int noteId, CancellationToken cancellationToken)
        {
            string categoryLine = !string.IsNullOrWhiteSpace(note.noteText.category) ?
                $"({note.noteText.category})\n" :
                string.Empty;

            string createdTimeLine = m_showCreatedTime ?
                $"created: {note.createdTime:yyyy.MM.dd HH:mm:ss tt}\n" :
                string.Empty;

            string messageText =
                categoryLine +
                createdTimeLine +
                note.noteText.text;

            return await m_botClient.SendTextMessageAsync(
                    chatId: m_chatId,
                    text: messageText,
                    replyMarkup: GetInlineKeyboardMarkup(noteId, note.noteText),
                    cancellationToken: cancellationToken,
                    disableWebPagePreview: true);
        }

        async Task DeleteMessage(int messageId, CancellationToken cancellationToken)
        {
            await m_botClient.DeleteMessageAsync(
                chatId: m_chatId,
                messageId: messageId,
                cancellationToken: cancellationToken);
        }

        static InlineKeyboardMarkup GetInlineKeyboardMarkup(int noteId, NoteText noteText)
        {
            return  new(new[]
                        {
                            new []
                            {
                                InlineKeyboardButton.WithSwitchInlineQueryCurrentChat(
                                    text: "edit",
                                    query: $"/edit {noteId} {"." + noteText.category + "\n" + noteText.text}"),
                                InlineKeyboardButton.WithSwitchInlineQueryCurrentChat(
                                    text: "delete",
                                    query: $"/delete {noteId}"),
                                InlineKeyboardButton.WithSwitchInlineQueryCurrentChat(
                                    text: "tag",
                                    query: $"/tag {noteId} {noteText.category}")
                            }
                        });
        }

        static IDeserializer GetDeserializer()
        {
            return new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
        }

        static ISerializer GetSerializer()
        {
            return new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
        }

        static void WriteYaml(object? data, string targetFilePath)
        {
            string yaml = GetSerializer()
                          .Serialize(data);
            using StreamWriter writer = new(targetFilePath);
            writer.Write(yaml);
        }

        static void LogToFile(string? username, string messageText)
        {
            System.IO.File.AppendAllLines(@"simple_log.txt", new[]
            {
                $"{username}: {DateTime.Now:yyyy.MM.dd HH:mm:ss}: {messageText}"
            });
        }

        static void LogDeletedNote(Note note)
        {
            string yaml = GetSerializer()
                              .Serialize(note);
            System.IO.File.AppendAllLines("deleted.txt", new[]
            {
                $"deleted at {DateTime.Now:yyyy.MM.dd HH:mm:ss}:\n{yaml}"
            });
        }
    }
}
