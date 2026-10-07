using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace ZenLead.Infrastructure.Ai;

/// <summary>
/// Makes one tiny completion shortly after startup so the first real compose request does not pay for DNS, TLS,
/// connection setup and JIT of the Semantic Kernel / OpenAI client path (the first call measured ~5 s vs ~2 s warm).
/// Fire-and-forget: it never delays startup and a failure is only logged. Costs a fraction of a cent per start.
/// </summary>
public class OpenAiWarmUpService(IChatCompletionService chat, ILogger<OpenAiWarmUpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield(); // let the host finish starting first
            var started = DateTime.UtcNow;

            var history = new ChatHistory();
            history.AddUserMessage("ping");
            await chat.GetChatMessageContentAsync(history, new OpenAIPromptExecutionSettings { MaxTokens = 1 }, kernel: null, stoppingToken);

            logger.LogInformation("OpenAI warm-up completed in {ElapsedMs} ms", (DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenAI warm-up failed; the first compose request will be slower. Continuing.");
        }
    }
}
