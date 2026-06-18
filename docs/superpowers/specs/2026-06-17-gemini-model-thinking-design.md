# Gemini Model Thinking — Expose Chain-of-Thought Reasoning to Customers

## Overview

Expose Gemini's internal chain-of-thought reasoning (model thinking) to customers in real-time during chatbot conversations, and preserve it in the message history accordion for post-hoc review.

## Architecture

```
Gemini API                            ChatbotService              QuoteEngine BFF              Blazor Client
────────────                          ──────────────              ────────────────              ─────────────
streamGenerateContent                 NDJSON stream               NDJSON stream                 
  SSE chunks                            │                           │                             
  │                                     │                           │                             
  ├─ parts[{text, thought:true}]  ──►   ├─ {type:"thought",  ──►   ├─ {type:"thought",   ──►    Render inline
  │                                     │    thought:"..."}         │    thought:"..."}           thinking bubble
  ├─ parts[{text}]               ──►   ├─ {type:"delta",    ──►   ├─ {type:"delta",     ──►    Append text
  │                                     │    delta:"..."}           │    delta:"..."}             
  │                                     │                           │                             
  └─ [end]                        ──►   └─ {type:"final",   ──►   └─ {type:"final",    ──►    Finalize
                                            message:{                 response:{                  
                                              thinkingSteps:            ThinkingSteps:              
                                                reasoning step           reasoning step            
                                            }                         }                           
                                         }                         }                             
```

## Streaming Contract

New NDJSON event type `thought` for real-time inline display:

```
{ "type": "started" }
{ "type": "thought", "thought": "The customer wants to manufacture 100 units of..." }
{ "type": "thought", "thought": "a CNC aluminum part based on the uploaded CAD file." }
{ "type": "delta",   "delta": "I can help you with your manufacturing request." }
{ "type": "delta",   "delta": " Let me review what you've provided..." }
{ "type": "final",   "message": { "content": "...", "thinkingSteps": [...] } }
```

Thought content also flows into the final `MessageResponse.ThinkingSteps` / `QuoteAgentTurnResponse.ThinkingSteps` for the post-hoc accordion.

## Detailed Changes

### ChatbotService — Gemini SDK Layer

#### 1. `IGeminiClient.cs` — DTO Changes

| Property | Class | Type | Notes |
|----------|-------|------|-------|
| `IncludeThoughts` | `GeminiRequest` | `bool` | Flag to enable model thinking |
| `ThoughtContent` | `GeminiResponse` | `string?` | Accumulated thought text from response |
| `Thought` | `GeminiStreamEvent` | `string?` | Incremental thought delta during streaming |

#### 2. `GeminiClient.cs` — Payload & Parsing

**`BuildGeminiPayloadJson`**: When `request.IncludeThoughts == true`, add `thinkingConfig` to the JSON payload:
```json
"thinkingConfig": { "includeThoughts": true }
```

**`ParseGeminiResponse`**: When iterating `parts`, check each part for `"thought": true`. Separate thought text from regular text:
- Parts with `thought: true` → accumulated into `ThoughtContent`
- Parts without → accumulated into `Content` (existing behavior)

**`StreamMessageAsync`**: After parsing each SSE chunk, emit thought deltas alongside text deltas. Accumulate full thought text for final event.

#### 3. `SendMessageCommand.cs` — New Callback

Add `ThoughtDeltaCallback` property (`Func<string, Task>?`) to relay thought deltas to the controller for NDJSON emission.

#### 4. `SendMessageCommandHandler.cs` — Streaming Handler

In `SendGeminiMaybeStreamingAsync`, wire `ThoughtDeltaCallback` to invoke the controller's thought-writer. Accumulate thought text for the final response.

### ChatbotService — NDJSON Contract

#### 5. `MessageStreamEvent` DTO

Add `Thought` property (`string?`) for `"thought"`-type events.

#### 6. `MessagesController.cs` — NDJSON Emission

Emit `{ "type": "thought", "thought": "..." }` events via `ThoughtDeltaCallback`.

In `MapMessageResponse`, map accumulated thought content into a `ThinkingStepResponse` with `Type = "reasoning"`.

### QuoteEngine BFF — Relay Layer

#### 7. `ChatbotMessageStreamEvent` DTO

Add `Thought` property (`string?`). Deserialization already generic — no parser changes needed.

#### 8. `QuoteAgentStreamEvent` DTO

Add `Thought` property (`string?`) for `"thought"`-type events.

#### 9. `QuoteAgentService.StreamAsync()`

Add handler for `"thought"` events to relay them to the BFF client stream. Accumulate thought content for the final `QuoteAgentTurnResponse.ThinkingSteps`.

### Client — Blazor WASM

#### 10. `AgentMessageRow` — New Fields

- `ModelThoughtContent` (`string`) — accumulated thought text for the current message
- `IsThinking` (`bool`) — whether the model is currently generating thought

#### 11. Streaming Loop — New Handler

In `SendAgentMessageStreamAsync` consumption loop, handle `"thought"` events by appending to `ModelThoughtContent` and setting `IsThinking = true`.

#### 12. Message Card — Real-time Rendering

Show a `<details class="qe-agent-model-thinking" open>` section with the accumulated thought content when `IsThinking && !string.IsNullOrWhiteSpace(ModelThoughtContent)`.

#### 13. Final Event — Preserve Thoughts

When `"final"` arrives, set `IsThinking = false` and add a `QuoteAgentThinkingStepDto` with `Type = "reasoning"` to the response's `ThinkingSteps`.

#### 14. CSS

Distinct styling for the model thinking bubble: blue-tinted background, italic text, open by default during streaming.

## Configuration

No new configuration keys needed. `IncludeThoughts` is hardcoded to `true` in the `GeminiRequest` construction within `SendMessageCommandHandler`. The Gemini model default budget is used (no explicit `thinkingBudget` set).

## Testing

### ChatbotService Tests

| Test | Description |
|------|-------------|
| `ParseGeminiResponse_WithThoughtParts_ExtractsThoughtContent` | Verify that `thought: true` text is separated from regular text |
| `StreamMessageAsync_YieldsThoughtEvents` | Verify thought deltas are yielded as separate stream events |
| `BuildGeminiPayloadJson_WithIncludeThoughts_IncludesThinkingConfig` | Verify `thinkingConfig` appears in payload |
| `MapMessageResponse_WithThoughtContent_CreatesReasoningStep` | Verify thought text maps to `ThinkingStepResponse` |

### BFF Tests

| Test | Description |
|------|-------------|
| `StreamAsync_RelaysThoughtEvents` | Verify thought events pass through to client NDJSON stream |
| `FinalResponse_IncludesThoughtSteps` | Verify accumulated thoughts appear in `ThinkingSteps` |

### Client Tests

| Test | Description |
|------|-------------|
| `ThoughtEvents_AccumulateInMessageRow` | Verify thought deltas concatenate correctly |
| `FinalEvent_AddsReasoningThinkingStep` | Verify reasoning step added to `ThinkingSteps` |

## Edge Cases

- **No thought content**: When Gemini doesn't produce thoughts (fast queries), no `thought` events are emitted — the UI shows nothing extra.
- **Interleaved thought and text chunks**: Though rare, if a single SSE chunk contains both thought and non-thought parts, both are emitted in the correct order within the stream.
- **Error/fallback responses**: If Gemini fails or returns a safety-blocked response, no thought events are emitted. Existing error handling unchanged.
- **Non-QuoteEngine channels**: The `IncludeThoughts` flag is set in `SendMessageCommandHandler`'s `GeminiRequest` construction — it applies globally to all chatbot conversations, not just QuoteEngine agent sessions.
