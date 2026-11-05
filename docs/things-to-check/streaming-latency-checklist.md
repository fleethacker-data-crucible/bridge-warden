# Streaming Latency Fix Checklist (C# Backend + HTML/JS Frontend)

## Goal
The first token should appear in the browser almost as soon as the provider sends it. Right now there's about a 10-second delay before anything shows up.

## 1. Provider call (C# backend)
- [ ] Request body includes `"stream": true`.
- [ ] Use `HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)`.
- [ ] Do NOT use `PostAsync`, `ReadAsStringAsync`, or `ReadFromJsonAsync` for streaming calls.
- [ ] Read the body with `ReadAsStreamAsync()` + `StreamReader.ReadLineAsync()` line by line.
- [ ] Parse each `data:` line as it arrives; stop on `data: [DONE]`.
- [ ] Reuse a single `HttpClient` (or `IHttpClientFactory`), not a new one per request.

## 2. Forwarding to the browser (ASP.NET)
- [ ] Set `Content-Type: text/event-stream`.
- [ ] Set `Cache-Control: no-cache`.
- [ ] Call `await Response.Body.FlushAsync()` after EVERY chunk written.
- [ ] Disable response compression for the streaming endpoint.
- [ ] Disable response buffering (e.g. `HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering()`).
- [ ] If behind nginx/IIS/proxy: add `X-Accel-Buffering: no` and disable proxy buffering.

## 3. Frontend (JS)
- [ ] Use `fetch()` and read with `response.body.getReader()` + `TextDecoder`.
- [ ] Do NOT use `await response.json()` or `await response.text()`.
- [ ] Append text to the UI as each chunk arrives.

## 4. Prompt size
- [ ] Log the token/character count of each request.
- [ ] Check that the system prompt and chat history aren't unexpectedly large.

## 5. Measure
- [ ] Log timestamps: request sent, provider headers received, first chunk from provider, first chunk flushed to browser.
- [ ] Compare against a direct `curl -N` call to the provider with `"stream": true`.
- [ ] Find which step accounts for the delay.

## 6. Gemini thinking delay (likely cause of the ~10s wait)
- [ ] Gemini 3.x models always reason before answering; thinking can be reduced but not fully disabled.
- [ ] Add `"reasoning_effort": "low"` (or `"none"`) to the request and compare time-to-first-token.
- [ ] Only use valid values: `high`, `low`, `medium`, `none` (`minimal` has been reported to return a 400 error).

## 7. Show thinking in the UI
- [ ] Show a "Thinking..." indicator as soon as the request is sent.
- [ ] Hide the indicator when the first real content chunk arrives.
- [ ] Optional: request thought summaries via the Gemini extension in the request body:
      `"extra_body": { "google": { "thinking_config": { "include_thoughts": true } } }`
      (verify the exact field name against Google's OpenAI-compatibility docs before shipping).
- [ ] Render thought chunks in a separate, collapsible "Thinking" section, apart from the final answer.
- [ ] Make sure the parser handles thought chunks without breaking normal `delta.content` handling.
