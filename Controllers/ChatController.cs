// ═══════════════════════════════════════════════════════════════════════
//  ChatController.cs  —  Fixed version with 11 corrections explained inline
// ═══════════════════════════════════════════════════════════════════════

using Google.Cloud.Dialogflow.V2;
using Microsoft.AspNetCore.Mvc;


// ── FIX 8 ────────────────────────────────────────────────────────────────
// BEFORE: No namespace.
// PROBLEM: Without a namespace, this class lives in the global namespace.
//          In a real project this causes name conflicts and makes it
//          impossible to use the class in unit tests without ambiguity.
// AFTER:
namespace HealthBuddyWebhook.Controllers;


public class ChatController : Controller
{
    // ── FIX 9 ────────────────────────────────────────────────────────────
    // BEFORE: SessionsClient, project ID, and credentials were all created
    //         or set inside SendMessage() on every single HTTP request.
    //
    // PROBLEM (SessionsClient): Explained in Program.cs Fix 3 above.
    //          One new gRPC connection per message = ~800ms overhead + resource waste.
    //
    // PROBLEM (IConfiguration): Reading config values inside action methods
    //          bypasses the DI system and makes the controller impossible to
    //          unit test (you can't inject a test configuration).
    //
    // PROBLEM (ILogger): Console.WriteLine goes nowhere in production hosting
    //          environments (IIS, Docker, Azure App Service). It never appears
    //          in Application Insights, log files, or any monitoring dashboard.
    //          ILogger<T> integrates with every logging provider automatically.
    //
    // AFTER: Declare all dependencies as readonly fields, inject via constructor.
    private readonly SessionsClient _sessionsClient;
    private readonly string _projectId;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        SessionsClient sessionsClient,          // Injected Singleton from Program.cs
        IConfiguration configuration,           // Reads appsettings.json
        ILogger<ChatController> logger)         // Built-in ASP.NET Core logging
    {
        _sessionsClient = sessionsClient;

        // ── FIX 10 ───────────────────────────────────────────────────────
        // BEFORE: "abra-ka-dabra-xhsp" hardcoded as a string literal in code.
        // PROBLEM: Hardcoded config in source code means:
        //          - Committing sensitive values to Git history.
        //          - Must recompile to change the project ID.
        //          - Cannot have different IDs for dev/staging/prod.
        // AFTER: Read from appsettings.json via IConfiguration.
        _projectId = configuration["Dialogflow:ProjectId"]
            ?? throw new InvalidOperationException(
                "Dialogflow:ProjectId is not set in appsettings.json. " +
                "Add a Dialogflow section with ProjectId.");
        // WHY the null-throw: Failing loudly at startup is far better than
        // failing silently at runtime. If the key is missing, the app refuses
        // to start and shows you exactly what to fix, rather than serving
        // chat messages that always return errors.

        _logger = logger;
    }


    // GET: /Chat  or  /  (default route)
    public IActionResult Index()
    {
        return View();
    }


    // ── FIX 11 ───────────────────────────────────────────────────────────
    // BEFORE: public JsonResult SendMessage(string userMessage, string sessionId)
    //
    // PROBLEM 1 — JsonResult return type:
    //   JsonResult is a concrete type. Returning IActionResult is the
    //   ASP.NET Core convention because it allows the method to return
    //   different result types (Json, BadRequest, StatusCode, etc.) without
    //   changing the signature. Unit tests can also assert on IActionResult
    //   using pattern matching.
    //
    // PROBLEM 2 — synchronous method:
    //   DetectIntent() (the sync version) blocks the current thread while
    //   waiting for Google's servers to respond — typically 100–500ms.
    //   ASP.NET Core has a fixed thread pool. Under load, blocking threads
    //   starves the pool and causes new requests to queue up waiting for
    //   a free thread. This is the #1 cause of throughput collapse in
    //   ASP.NET Core apps. async/await releases the thread back to the pool
    //   during the await, so the same thread serves other requests while
    //   this one waits for the network.
    //
    // AFTER:
    [HttpPost]
    public async Task<IActionResult> SendMessage(string userMessage, string sessionId)
    {
        // ── FIX 12 ───────────────────────────────────────────────────────
        // BEFORE: No input validation at all.
        // PROBLEM: Passing a null or empty userMessage directly to the
        //          Dialogflow SDK throws a NullReferenceException or an
        //          RpcException from the gRPC layer — both of which are
        //          unhandled at this point in the original code.
        //          A null sessionId causes the SessionName constructor to throw.
        // AFTER: Validate both inputs and return a friendly error early.
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return Json(new { success = false, reply = "Please type a message first." });
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning("SendMessage called with missing sessionId.");
            return Json(new { success = false, reply = "Session error. Please refresh the page." });
        }

        // ── FIX 13 ───────────────────────────────────────────────────────
        // BEFORE: No length limit on userMessage.
        // PROBLEM: Dialogflow ES TextInput has a hard limit of 256 characters.
        //          Sending more throws an RpcException. Accepting unbounded
        //          input also forwards potentially large strings to a paid
        //          external API on every request.
        // NOTE: The previous version used 500 — that was wrong. 500 still
        //       exceeds Dialogflow ES's 256-char limit and would still throw.
        // AFTER: Cap at 256 to match the actual Dialogflow ES limit.
        const int MaxMessageLength = 256;
        if (userMessage.Length > MaxMessageLength)
        {
            userMessage = userMessage[..MaxMessageLength];   // Trim, don't reject
        }

        userMessage = userMessage.Trim();

        try
        {
            var session = new SessionName(_projectId, sessionId);

            // ── FIX 14 ───────────────────────────────────────────────────
            // BEFORE: client.DetectIntent() — synchronous, blocking.
            // AFTER:  await _sessionsClient.DetectIntentAsync() — non-blocking.
            // WHY: Explained fully in Fix 11 above. The Async version does
            //      the exact same work but releases the thread during the
            //      network round-trip, allowing it to handle other requests.
            var response = await _sessionsClient.DetectIntentAsync(
                session: session,
                queryInput: new QueryInput
                {
                    Text = new TextInput
                    {
                        Text = userMessage,
                        LanguageCode = "en"
                    }
                }
            );

            // ── FIX 15 ───────────────────────────────────────────────────
            // BEFORE: string botReply = response.QueryResult.FulfillmentText;
            //         (used directly with no null check)
            // PROBLEM: FulfillmentText is null or empty in two real scenarios:
            //   a) The matched intent uses a webhook and the webhook call fails.
            //      Dialogflow returns the intent match but no fulfillment text.
            //   b) The intent has no response configured at all.
            //   Both cases silently return "" to the browser, which renders
            //   as an empty bot bubble in the chat UI — very confusing.
            // AFTER: Provide a clear fallback message.
            var botReply = response.QueryResult.FulfillmentText;
            if (string.IsNullOrWhiteSpace(botReply))
            {
                botReply = "I didn't quite understand that. Could you try rephrasing?";
            }

            // ── FIX 16 ───────────────────────────────────────────────────
            // BEFORE: Console.WriteLine — missing in production, no structure.
            // AFTER:  ILogger with structured logging.
            // WHY: This log entry automatically flows to Application Insights,
            //      Serilog, NLog, Azure Monitor, or any other provider configured
            //      in Program.cs, with proper timestamps and log levels.
            //      {Intent} and {SessionId} are named properties in structured
            //      logging — query them later in your logging dashboard.
            _logger.LogInformation(
                "Dialogflow intent matched: {Intent} | Session: {SessionId}",
                response.QueryResult.Intent?.DisplayName ?? "none",
                sessionId);

            return Json(new { success = true, reply = botReply });
        }
        catch (Grpc.Core.RpcException ex)
        {
            // ── FIX 17 ───────────────────────────────────────────────────
            // BEFORE: catch (Exception ex) with Console.WriteLine.
            // PROBLEM: Catching the base Exception type swallows every kind
            //          of error with the same message. You can't distinguish
            //          "Google auth failed" from "network timeout" from
            //          "Dialogflow quota exceeded" — all look identical in logs.
            // AFTER: Catch the specific gRPC exception type first, then fall
            //        through to a general catch for truly unexpected errors.
            //        ILogger.LogError() records the full exception object
            //        including stack trace, not just the message string.
            _logger.LogError(
                ex,
                "Dialogflow gRPC error. StatusCode: {StatusCode} | Session: {SessionId}",
                ex.StatusCode,
                sessionId);

            return Json(new
            {
                success = false,
                reply = "The health assistant is temporarily unavailable. Please try again in a moment."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in SendMessage | Session: {SessionId}", sessionId);

            return Json(new
            {
                success = false,
                reply = "Something went wrong. Please refresh the page and try again."
            });
        }
    }
}