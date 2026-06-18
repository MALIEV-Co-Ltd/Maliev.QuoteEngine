# Gemini Model Thinking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose Gemini's internal chain-of-thought reasoning to customers in real-time during chatbot conversations and preserve it in the message history accordion.

**Architecture:** Hybrid approach — thought blocks flow as inline NDJSON `thought` events for real-time display AND are accumulated into the final `ThinkingSteps` list for the post-hoc accordion.

**Tech Stack:** .NET 10, ASP.NET Core, Blazor WASM, Gemini API (raw HTTP), System.Text.Json, SignalR, xUnit, Testcontainers

---

### Task 1: ChatbotService — GeminiRequest DTO

**Files:**
- Modify: `Maliev.ChatbotService.Application\Interfaces\IGeminiClient.cs`

- [ ] **Step 1: Add IncludeThoughts to GeminiRequest**

Add `IncludeThoughts` property to `GeminiRequest`:
```csharp
/// <summary>Gets or sets whether to request model thinking/reasoning blocks.</summary>
public bool IncludeThoughts { get; set; }
```

- [ ] **Step 2: Add ThoughtContent to GeminiResponse**

Add `ThoughtContent` property to `GeminiResponse`:
```csharp
/// <summary>Gets or sets the accumulated thought/reasoning text from the model.</summary>
public string? ThoughtContent { get; set; }
```

- [ ] **Step 3: Add Thought to GeminiStreamEvent**

Add `Thought` property to `GeminiStreamEvent`:
```csharp
/// <summary>Gets or sets incremental thought text (for thought-type events).</summary>
public string? Thought { get; set; }
```

- [ ] **Step 4: Commit**

```bash
git add Maliev.ChatbotService.Application/Interfaces/IGeminiClient.cs
git commit -m "feat: add thought content DTOs to Gemini request/response/stream"
```

---

### Task 2: ChatbotService — GeminiClient Payload & Parsing

**Files:**
- Modify: `Maliev.ChatbotService.Infrastructure\AI\GeminiClient.cs`

- [ ] **Step 1: Add thinkingConfig to BuildGeminiPayloadJson**

In `BuildGeminiPayloadJson` (line 443), after building the payload object for each branch, conditionally add `thinkingConfig`:

In all three payload branches (line 494-504, 527-536, 539-544), add:
```csharp
// After the base payload object creation in each branch:
if (request.IncludeThoughts)
{
    // We need to merge thinkingConfig into the generated payload
    // The simplest approach: wrap generationConfig and add thinkingConfig alongside it
}
```

However, since anonymous types can't be easily merged, restructure `BuildGeminiPayloadJson` to build a `Dictionary<string, object?>` then serialize at the end:

```csharp
private static string BuildGeminiPayloadJson(GeminiRequest request)
{
    // ... build contentsParts, toolsList as before ...
    
    var payload = new Dictionary<string, object?>
    {
        ["systemInstruction"] = new { parts = new[] { new { text = request.SystemInstruction } } },
        ["contents"] = contentsParts.ToArray()
    };

    if (!string.IsNullOrEmpty(request.ResponseMimeType))
    {
        payload["generationConfig"] = new
        {
            responseMimeType = request.ResponseMimeType,
            responseSchema = request.ResponseSchema
        };
    }

    if (hasTools || useBuiltInSearch)
    {
        // ... build tools list ...
        payload["tools"] = toolsList;
        payload["toolConfig"] = new
        {
            functionCallingConfig = new { mode = request.ToolConfig?.Mode ?? "AUTO" }
        };
    }

    if (request.IncludeThoughts)
    {
        payload["thinkingConfig"] = new { includeThoughts = true };
    }

    return JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
}
```

- [ ] **Step 2: Detect thought blocks in ParseGeminiResponse**

In `ParseGeminiResponse` (line 553), modify the parts loop to separate thought text:

```csharp
var textParts = new List<string>();
var thoughtParts = new List<string>();  // NEW
var functionCalls = new List<GeminiFunctionCall>();
if (firstCandidate.TryGetProperty("content", out var contentProp) &&
    contentProp.TryGetProperty("parts", out var parts))
{
    foreach (var part in parts.EnumerateArray())
    {
        if (part.TryGetProperty("text", out var textProp))
        {
            var isThought = part.TryGetProperty("thought", out var thoughtFlag) &&
                            thoughtFlag.ValueKind == JsonValueKind.True &&
                            thoughtFlag.GetBoolean();
            if (isThought)
                thoughtParts.Add(textProp.GetString() ?? string.Empty);
            else
                textParts.Add(textProp.GetString() ?? string.Empty);
        }
        else if (part.TryGetProperty("functionCall", out var fcProp))
        {
            // ... existing functionCall handling ...
        }
    }
}

return new GeminiResponse
{
    Success = true,
    Content = string.Join("", textParts),
    ThoughtContent = string.Join("", thoughtParts),  // NEW
    FunctionCalls = functionCalls,
    TokenUsage = tokenUsage
};
```

- [ ] **Step 3: Emit thought events in StreamMessageAsync**

In `StreamMessageAsync` (line 310), after the existing delta emission at line 378-386, add thought emission:

```csharp
var accumulatedText = new StringBuilder();
var accumulatedThought = new StringBuilder();  // NEW
// ... existing code ...

// In the SSE parsing loop, after the existing delta emission:
if (!string.IsNullOrEmpty(parsed.ThoughtContent))
{
    accumulatedThought.Append(parsed.ThoughtContent);
    yield return new GeminiStreamEvent
    {
        Type = "thought",
        Thought = parsed.ThoughtContent
    };
}

// In the final event, set ThoughtContent on the response:
finalResponse.ThoughtContent = accumulatedThought.ToString();
```

- [ ] **Step 4: Commit**

```bash
git add Maliev.ChatbotService.Infrastructure/AI/GeminiClient.cs
git commit -m "feat: add thinkingConfig to payload and parse thought blocks from Gemini response"
```

---

### Task 3: ChatbotService — SendMessageCommand and Handler

**Files:**
- Modify: `Maliev.ChatbotService.Application\Commands\SendMessageCommand.cs`
- Modify: `Maliev.ChatbotService.Application\Handlers\SendMessageCommandHandler.cs`

- [ ] **Step 1: Add ThoughtDeltaCallback to SendMessageCommand**

```csharp
/// <summary>
/// Optional callback for streaming model thought deltas to the caller.
/// </summary>
public Func<string, Task>? ThoughtDeltaCallback { get; set; }
```

- [ ] **Step 2: Wire IncludeThoughts in SendMessageCommandHandler**

In `SendMessageCommandHandler.HandleAsync` (line 392), set `IncludeThoughts = true` on the Gemini request:

```csharp
var geminiRequest = new GeminiRequest
{
    ModelName = command.ModelName,
    SystemInstruction = systemInstructionText,
    Messages = conversationHistory ...,
    TimeoutSeconds = enableGeminiSearch ? 30 : 10,
    ResponseMimeType = command.ResponseMimeType,
    ResponseSchema = command.ResponseSchema,
    EnableWebSearch = enableGeminiSearch,
    IncludeThoughts = true  // NEW
};
```

- [ ] **Step 3: Wire thought delta callback in SendGeminiMaybeStreamingAsync**

In `SendGeminiMaybeStreamingAsync` (line 609), add thought delta handling alongside text delta:

```csharp
private async Task<GeminiResponse> SendGeminiMaybeStreamingAsync(
    GeminiRequest request,
    Func<string, Task>? onTextDelta,
    Func<string, Task>? onThoughtDelta,  // NEW parameter
    CancellationToken cancellationToken)
{
    var accumulatedThought = new StringBuilder();  // NEW
    // ... existing streaming loop ...
    await foreach (var streamEvent in _geminiClient.StreamMessageAsync(request, cancellationToken))
    {
        if (streamEvent.Type.Equals("delta", StringComparison.OrdinalIgnoreCase) && ...)
        {
            await onTextDelta(streamEvent.Delta);
        }
        else if (streamEvent.Type.Equals("thought", StringComparison.OrdinalIgnoreCase) &&  // NEW
                 !string.IsNullOrEmpty(streamEvent.Thought) && onThoughtDelta != null)
        {
            accumulatedThought.Append(streamEvent.Thought);
            await onThoughtDelta(streamEvent.Thought);
        }
        // ... existing final/error handling ...
    }

    if (finalResponse != null)
    {
        finalResponse.ThoughtContent = accumulatedThought.ToString();
    }
}
```

Update the two call sites (agent path and regular path) to pass `command.ThoughtDeltaCallback`.

- [ ] **Step 4: Commit**

```bash
git add Maliev.ChatbotService.Application/Commands/SendMessageCommand.cs Maliev.ChatbotService.Application/Handlers/SendMessageCommandHandler.cs
git commit -m "feat: wire thought delta callback through streaming pipeline"
```

---

### Task 4: ChatbotService — NDJSON Contract (Controller Layer)

**Files:**
- Modify: `Maliev.ChatbotService.Api\Models\Responses\MessageResponse.cs`
- Modify: `Maliev.ChatbotService.Api\Controllers\V1\MessagesController.cs`

- [ ] **Step 1: Add Thought to MessageStreamEvent**

```csharp
public class MessageStreamEvent
{
    public string Type { get; set; } = "delta";
    public string? Delta { get; set; }
    public string? Thought { get; set; }  // NEW: for thought-type events
    public MessageResponse? Message { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 2: Wire thought callback in StreamMessage endpoint**

In `MessagesController.StreamMessage` (line 114), after the existing `TextDeltaCallback` at line 131, add:

```csharp
command.ThoughtDeltaCallback = thought => WriteStreamEventAsync(new MessageStreamEvent
{
    Type = "thought",
    Thought = thought
}, cancellationToken);
```

- [ ] **Step 3: Map thought content to ThinkingSteps in final response**

In `MapMessageResponse` (line 233), add thought content from `result` to thinking steps:

```csharp
private static MessageResponse MapMessageResponse(SendMessageResult result)
{
    var response = new MessageResponse
    {
        MessageId = result.MessageId,
        Content = result.Content,
        Role = result.Role == MessageRole.Assistant ? "assistant" : "user",
        // ... other fields ...
    };

    // Map agent thinking steps
    if (result.ThinkingSteps?.Count > 0)
    {
        response.ThinkingSteps.AddRange(result.ThinkingSteps.Select(s => new ThinkingStepResponse
        {
            StepNumber = s.StepNumber,
            Type = s.Type,
            Title = s.Title,
            Detail = s.Detail,
            Timestamp = s.Timestamp,
            DurationMs = s.DurationMs
        }));
    }

    // Add model reasoning step if thought content exists  // NEW
    if (!string.IsNullOrEmpty(result.ThoughtContent))
    {
        var maxStep = response.ThinkingSteps.Count > 0
            ? response.ThinkingSteps.Max(s => s.StepNumber)
            : 0;
        response.ThinkingSteps.Add(new ThinkingStepResponse
        {
            StepNumber = maxStep + 1,
            Type = "reasoning",
            Title = "Model reasoning",
            Detail = result.ThoughtContent,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    return response;
}
```

Note: Add `ThoughtContent` to the result model if needed, or pass it through from the `GeminiResponse`.

- [ ] **Step 4: Commit**

```bash
git add Maliev.ChatbotService.Api/Models/Responses/MessageResponse.cs Maliev.ChatbotService.Api/Controllers/V1/MessagesController.cs
git commit -m "feat: emit thought NDJSON events and map model reasoning to thinking steps"
```

---

### Task 5: QuoteEngine BFF — Relay Layer DTOs

**Files:**
- Modify: `Maliev.QuoteEngine.Bff\Clients\ChatbotServiceClient.cs`
- Modify: `Maliev.QuoteEngine.Shared\Agent\QuoteAgentDtos.cs`

- [ ] **Step 1: Add Thought to ChatbotMessageStreamEvent**

```csharp
public sealed class ChatbotMessageStreamEvent
{
    public string Type { get; set; } = "delta";
    public string? Delta { get; set; }
    public string? Thought { get; set; }  // NEW
    public ChatbotMessageResponse? Message { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 2: Add Thought to QuoteAgentStreamEvent**

```csharp
public sealed class QuoteAgentStreamEvent
{
    [Required]
    [StringLength(40)]
    public string Type { get; set; } = "delta";
    public string? Delta { get; set; }
    public string? Thought { get; set; }  // NEW
    public QuoteAgentTurnResponse? Response { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 3: Commit**

```bash
git add Maliev.QuoteEngine.Bff/Clients/ChatbotServiceClient.cs Maliev.QuoteEngine.Shared/Agent/QuoteAgentDtos.cs
git commit -m "feat: add Thought property to stream event DTOs"
```

---

### Task 6: QuoteEngine BFF — Relay Thought Events in Streaming

**Files:**
- Modify: `Maliev.QuoteEngine.Bff\Services\QuoteAgentService.cs`

- [ ] **Step 1: Add thought handler in StreamAsync**

In `StreamAsync` (line 154), add thought handler alongside delta handler at line 216:

```csharp
else if (streamEvent.Type.Equals("thought", StringComparison.OrdinalIgnoreCase) &&
         !string.IsNullOrEmpty(streamEvent.Thought))
{
    yield return new QuoteAgentStreamEvent
    {
        Type = "thought",
        Thought = streamEvent.Thought
    };
}
```

- [ ] **Step 2: Accumulate thoughts for final response**

Add a `StringBuilder? _accumulatedThought` field before the stream loop, or use a local variable in the method scope:

```csharp
var accumulatedThought = new StringBuilder();
// ... in the thought handler:
accumulatedThought.Append(streamEvent.Thought);
```

In the final response construction (line 251), add model reasoning steps:

```csharp
if (accumulatedThought.Length > 0)
{
    var existingSteps = EnrichSteps(finalMessage?.ThinkingSteps);
    var maxStep = existingSteps.Count > 0 ? existingSteps.Max(s => s.StepNumber) : 0;
    var thoughtSteps = new List<QuoteAgentThinkingStepDto>(existingSteps)
    {
        new()
        {
            StepNumber = maxStep + 1,
            Type = "reasoning",
            Title = "Model reasoning",
            Detail = accumulatedThought.ToString(),
            Timestamp = DateTimeOffset.UtcNow
        }
    };
    response.ThinkingSteps = thoughtSteps;
}
else
{
    response.ThinkingSteps = EnrichSteps(finalMessage?.ThinkingSteps);
}
```

- [ ] **Step 3: Commit**

```bash
git add Maliev.QuoteEngine.Bff/Services/QuoteAgentService.cs
git commit -m "feat: relay thought events and accumulate for final response"
```

---

### Task 7: QuoteEngine Client — Handle Thought Events in Streaming

**Files:**
- Modify: `Maliev.QuoteEngine.Client\Components\QuoteAgent\QuoteAgentLaunchShell.razor`
- Modify: `Maliev.QuoteEngine.Client\Components\QuoteAgent\QuoteAgentLaunchShell.razor.css` (if separate file)

- [ ] **Step 1: Add ModelThoughtContent tracking in AgentMessageRow**

In the `AgentMessageRow` class (defined in the file), add:
```csharp
public string ModelThoughtContent { get; set; } = string.Empty;
public bool IsThinking { get; set; }
```

- [ ] **Step 2: Add thought handler in streaming loop**

In the `SendMessageAsync` method's stream event loop (~line 2182), add:

```csharp
else if (streamEvent.Type.Equals("thought", StringComparison.OrdinalIgnoreCase) &&
         !string.IsNullOrWhiteSpace(streamEvent.Thought))
{
    assistantMessage.ModelThoughtContent += streamEvent.Thought;
    assistantMessage.IsThinking = true;
    await InvokeAsync(StateHasChanged);
}
```

- [ ] **Step 3: Add reasoning step on final event**

Before the `final` response's `ThinkingSteps` are assigned, add:

```csharp
// In the "final" handler (~line 2187):
if (!string.IsNullOrWhiteSpace(assistantMessage.ModelThoughtContent))
{
    var thoughtStep = new QuoteAgentThinkingStepDto
    {
        StepNumber = (streamEvent.Response.ThinkingSteps?.Count ?? 0) + 1,
        Type = "reasoning",
        Title = "Model reasoning",
        Detail = assistantMessage.ModelThoughtContent,
        Timestamp = DateTimeOffset.UtcNow
    };
    if (streamEvent.Response.ThinkingSteps is { } steps)
        steps.Add(thoughtStep);
    else
        streamEvent.Response.ThinkingSteps = [thoughtStep];
}
assistantMessage.IsThinking = false;
```

- [ ] **Step 4: Add real-time thinking rendering in the message card**

Around the existing thinking steps rendering (~line 499), add:

```razor
@if (message.IsThinking && !string.IsNullOrWhiteSpace(message.ModelThoughtContent))
{
    <details class="qe-agent-model-thinking" open>
        <summary>
            <span>@Text("Model reasoning…", "​กำลังคิด...")</span>
        </summary>
        <div class="qe-agent-model-thinking-content">@message.ModelThoughtContent</div>
    </details>
}
```

- [ ] **Step 5: Add CSS styles**

In the component's CSS section, add:

```css
.qe-agent-model-thinking {
    background: var(--mud-palette-info-hover, rgba(33, 150, 243, 0.08));
    border: 1px solid var(--mud-palette-info-default, #2196f3);
    border-radius: 8px;
    margin: 8px 0;
    padding: 8px 12px;
}

.qe-agent-model-thinking summary {
    cursor: pointer;
    font-size: 0.85rem;
    font-weight: 600;
    opacity: 0.8;
}

.qe-agent-model-thinking-content {
    font-size: 0.82rem;
    line-height: 1.5;
    margin-top: 6px;
    opacity: 0.75;
    font-style: italic;
    white-space: pre-wrap;
}
```

- [ ] **Step 6: Commit**

```bash
git add Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor
git commit -m "feat: real-time model thought display and final reasoning step preservation"
```

---

### Task 8: ChatbotService Tests

**Files:**
- Create/Modify: `Maliev.ChatbotService.Tests\...\GeminiClientTests.cs`

- [ ] **Step 1: Write test for thought detection in ParseGeminiResponse**

```csharp
[Fact]
public void ParseGeminiResponse_WithThoughtParts_ExtractsThoughtContent()
{
    var json = /* language=json */
        """
        {
            "candidates": [{
                "content": {
                    "parts": [
                        {"text": "The customer wants 100 units", "thought": true},
                        {"text": "of CNC aluminum parts.", "thought": true},
                        {"text": "I can help you with your quote."}
                    ]
                }
            }],
            "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 20, "totalTokenCount": 30 }
        }
        """;

    using var doc = JsonDocument.Parse(json);
    var client = new GeminiClient(...); // arrange dependencies
    var result = client.ParseGeminiResponse(doc.RootElement); // need to make accessible

    Assert.Equal("I can help you with your quote.", result.Content);
    Assert.Equal("The customer wants 100 unitsof CNC aluminum parts.", result.ThoughtContent);
}
```

Note: This may require making `ParseGeminiResponse` internal/public or testing it through the public `StreamMessageAsync` interface.

- [ ] **Step 2: Test payload includes thinkingConfig**

```csharp
[Fact]
public void BuildGeminiPayloadJson_WithIncludeThoughts_IncludesThinkingConfig()
{
    var request = new GeminiRequest
    {
        IncludeThoughts = true,
        SystemInstruction = "Be helpful.",
        Messages = new List<GeminiMessage>
        {
            new() { Role = "user", Content = "Hello" }
        }
    };

    var json = GeminiClient.BuildGeminiPayloadJson(request); // may need to make internal
    using var doc = JsonDocument.Parse(json);
    var hasThinkingConfig = doc.RootElement.TryGetProperty("thinkingConfig", out var tc);
    Assert.True(hasThinkingConfig);
    Assert.True(tc.GetProperty("includeThoughts").GetBoolean());
}
```

- [ ] **Step 3: Commit**

```bash
git add Maliev.ChatbotService.Tests/...
git commit -m "test: add Gemini client model thought parsing and payload tests"
```

---

### Task 9: QuoteEngine BFF Tests

**Files:**
- Modify: `Maliev.QuoteEngine.Tests\QuoteAgentEndpointTests.cs`

- [ ] **Step 1: Add test for thought event relay**

```csharp
[Fact]
public async Task StreamAsync_RelaysThoughtEvents()
{
    // Arrange: mock ChatbotServiceClient to return a thought event
    // Act: consume QuoteAgentService.StreamAsync()
    // Assert: thought event appears in the output
}
```

- [ ] **Step 2: Add test for final response includes reasoning step**

```csharp
[Fact]
public async Task StreamAsync_AccumulatesThoughtsIntoFinalResponse()
{
    // Arrange: mock returns multiple thought deltas
    // Act: consume full stream
    // Assert: final response.ThinkingSteps contains a "reasoning" step with accumulated content
}
```

- [ ] **Step 3: Commit**

```bash
git add Maliev.QuoteEngine.Tests/...
git commit -m "test: add BFF thought event relay and accumulation tests"
```

---

### Task 10: Build, Verify, and Final Commit

- [ ] **Step 1: Build ChatbotService**

```bash
dotnet build Maliev.ChatbotService.slnx --configuration Release
```

- [ ] **Step 2: Run ChatbotService tests**

```bash
dotnet test Maliev.ChatbotService.slnx --configuration Release --filter "FullyQualifiedName~GeminiClientTests"
```

- [ ] **Step 3: Build QuoteEngine**

```bash
dotnet build Maliev.QuoteEngine.slnx --configuration Release
```

- [ ] **Step 4: Run QuoteEngine tests**

```bash
dotnet test Maliev.QuoteEngine.slnx --configuration Release --filter "FullyQualifiedName~QuoteAgentEndpointTests"
```

- [ ] **Step 5: Format check**

```bash
dotnet format Maliev.QuoteEngine.slnx --verify-no-changes
```

- [ ] **Step 6: Re-commit any fixes**

```bash
git add -A
git commit -m "chore: address build and test feedback"
```
