using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using NotesBot;

TelegramBotClient botClient = new(Environment.GetEnvironmentVariable("TOKEN") ?? string.Empty);
using CancellationTokenSource cts = new();

ReceiverOptions receiverOptions = new()
{
    AllowedUpdates = Array.Empty<UpdateType>() // receive all update types except ChatMember related updates
};

NotesHandler notesHandler = new(botClient, cts);
await notesHandler.LoadSavedNotes(cts.Token);

// StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    pollingErrorHandler: HandlePollingErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

User me = await botClient.GetMeAsync();
Console.WriteLine($"Start listening for @{me.Username}");
notesHandler.Wait();
Console.WriteLine("Stopping bot");
cts.Cancel(); // Send cancellation request to stop bot

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
    switch (update.Type)
    {
        case UpdateType.Message:
            {
                if (update.Message is { } message)
                {
                    await notesHandler.ProcessMessage(message, cancellationToken);
                }
            }
            break;
        //case UpdateType.CallbackQuery: { if (update.CallbackQuery is { } callbackQuery)
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    string ErrorMessage = exception switch
    {
        ApiRequestException apiRequestException
            => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
        _ => exception.ToString()
    };

    Console.WriteLine(ErrorMessage);
    return Task.CompletedTask;
}
