# Quote Agent Workbench Artifact Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clean up Quote Agent workbench artifacts by removing redundant viewer artifact entries, adding chat scroll-to-bottom behavior, and enriching uploaded 3D file metadata display.

**Architecture:** Keep changes localized to the existing Quote Agent Blazor component and its companion JavaScript. The Blazor component already owns message rendering, artifact panel rendering, and uploaded part state. Add small helper methods for artifact filtering, metadata formatting, and scroll interop. Add a small JS helper function for scrolling the chat thread and the floating scroll button visibility.

**Tech Stack:** Blazor WebAssembly, MudBlazor icons, JavaScript interop, existing Quote Agent DTOs.

---

### Task 1: Filter redundant viewer artifacts from the workbench artifact list

**Files:**
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\QuoteAgent\QuoteAgentLaunchShell.razor`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\css\app.css`
- Test: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\Maliev.QuoteEngine.Tests.csproj`

- [ ] **Step 1: Add a focused regression test for artifact filtering**

Add a test in the existing xUnit test project that creates a sample `QuoteAgentArtifactDto` list containing a viewer artifact and a non-viewer artifact, then verifies that the filtering helper excludes the viewer artifact while preserving other artifacts.

Example assertion:

```csharp
var artifacts = new[]
{
    new QuoteAgentArtifactDto { ArtifactType = "viewer", Title = "3D viewer - Ring1.stl", Status = "Ready" },
    new QuoteAgentArtifactDto { ArtifactType = "dfm", Title = "DFM analysis - Ring1.stl", Status = "Ready" }
};

var visible = artifacts.Where(IsVisibleQuoteAgentArtifact).ToList();

Assert.Single(visible);
Assert.Equal("dfm", visible[0].ArtifactType);
```

- [ ] **Step 2: Run the failing test**

Run:

```powershell
dotnet test Maliev.QuoteEngine.slnx --configuration Release --filter "FullyQualifiedName~QuoteAgentEndpointTests"
```

Expected: the new test fails because the filtering helper does not exist yet.

- [ ] **Step 3: Implement the filter**

Add a private helper method in `QuoteAgentLaunchShell.razor`:

```csharp
private static bool IsVisibleQuoteAgentArtifact(QuoteAgentArtifactDto artifact)
{
    return !artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase);
}
```

Update the artifact panel rendering to use it:

```razor
@foreach (var artifact in _artifacts.Where(IsVisibleQuoteAgentArtifact))
{
    <div class="@($"qe-agent-artifact {UiFocusClass(ArtifactFocusKey(artifact), ArtifactTypeFocusKey(artifact.ArtifactType))}")">
        <MudIcon Icon="@ArtifactIcon(artifact.ArtifactType)" Size="Size.Small" />
        <div>
            <strong>@artifact.Title</strong>
            <small>@artifact.Status</small>
        </div>
        @if (!string.IsNullOrWhiteSpace(artifact.Url))
        {
            <button type="button"
                    class="qe-agent-artifact-link"
                    @onclick="@(() => OpenArtifactPreview(artifact))">
                <span>@ArtifactActionLabel(artifact)</span>
                <MudIcon Icon="@Icons.Material.Outlined.Preview" Size="Size.Small" />
            </button>
        }
    </div>
}
```

- [ ] **Step 4: Run the test again**

Expected: PASS.

---

### Task 2: Add scroll-to-bottom chat behavior and floating button

**Files:**
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\QuoteAgent\QuoteAgentLaunchShell.razor`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\js\maliev-chatbot.js`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\css\app.css`
- Test: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\Maliev.QuoteEngine.Tests.csproj`

- [ ] **Step 1: Add a test for scroll-to-bottom helper**

Add a test that verifies the chat scroll helper marks the chat as scrolled away from the latest message. Since this is UI behavior, use a focused component or helper test if an existing pattern exists. If no suitable test exists, add a minimal helper method in the Blazor component and test it directly.

Example helper:

```csharp
private static bool IsChatAtBottom(double scrollTop, double scrollHeight, double clientHeight)
{
    return scrollHeight - scrollTop - clientHeight <= 2;
}
```

Test:

```csharp
Assert.True(IsChatAtBottom(100, 200, 100));
Assert.False(IsChatAtBottom(50, 200, 100));
```

- [ ] **Step 2: Run the failing test**

Expected: FAIL because the helper does not exist yet.

- [ ] **Step 3: Add JS scroll helper**

Add to `maliev-chatbot.js`:

```javascript
export function scrollToElementBottom(element, smooth) {
  if (!element) return;

  window.requestAnimationFrame(() => {
    element.scrollTo({
      top: element.scrollHeight,
      behavior: smooth ? 'smooth' : 'auto'
    });
  });
}
```

- [ ] **Step 4: Add scroll-to-bottom button to the Razor markup**

Place a floating button inside the chat thread container, after the message list:

```razor
@if (_showScrollToBottomButton)
{
    <button type="button"
            class="qe-agent-scroll-to-bottom"
            title="@Text("Scroll to latest message", "เลื่อนไปข้อความล่าสุด")"
            aria-label="@Text("Scroll to latest message", "เลื่อนไปข้อความล่าสุด")"
            @onclick="ScrollToLatestMessageAsync">
        <MudIcon Icon="@Icons.Material.Outlined.KeyboardArrowDown" Size="Size.Small" />
    </button>
}
```

- [ ] **Step 5: Add scroll event handler and methods**

Add a scroll handler to the chat thread element:

```razor
<div class="qe-agent-thread"
     @ref="_threadElement"
     @onscroll="OnThreadScrollAsync"
     aria-live="polite">
```

Add the backing code:

```csharp
private ElementReference _threadElement;
private bool _showScrollToBottomButton;

private async Task OnThreadScrollAsync()
{
    if (_threadElement.Context is null) return;

    var scrollInfo = await Js.InvokeAsync<ChatScrollInfo>("quoteAgentChat.getScrollInfo", _threadElement);
    _showScrollToBottomButton = !quoteAgentChat.isAtBottom(scrollInfo.scrollTop, scrollInfo.scrollHeight, scrollInfo.clientHeight);
    await InvokeAsync(StateHasChanged);
}

private async Task ScrollToLatestMessageAsync()
{
    await Js.InvokeVoidAsync("quoteAgentChat.scrollToElementBottom", _threadElement, true);
    _showScrollToBottomButton = false;
    await InvokeAsync(StateHasChanged);
}
```

Add the helper record:

```csharp
private sealed record ChatScrollInfo(double scrollTop, double scrollHeight, double clientHeight);
```

- [ ] **Step 6: Add CSS for the button**

Add styles for `.qe-agent-scroll-to-bottom` with a fixed/absolute position inside the thread, a clean circular pill shape, and hover state.

- [ ] **Step 7: Run the test again**

Expected: PASS.

---

### Task 3: Add 3D file metadata display (file size, volume, bounding box)

**Files:**
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Models\QuotePartViewModel.cs`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\QuoteAgent\QuoteAgentLaunchShell.razor`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\css\app.css`
- Test: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\Maliev.QuoteEngine.Tests.csproj`

- [ ] **Step 1: Add a test for metadata formatting**

Add a test for a helper that formats 3D metadata:

```csharp
var part = new QuotePartViewModel
{
    FileName = "Ring1.stl",
    FileSizeBytes = 1_234_567,
    VolumeCc = 12.34m,
    BoundingBox = new QuoteBoundingBoxDto(10.5m, 8.2m, 4.1m)
};

var metadata = FormatUploadedPartMetadata(part);

Assert.Contains("1.2 MB", metadata);
Assert.Contains("12.3 cc", metadata);
Assert.Contains("10.5 × 8.2 × 4.1 mm", metadata);
```

- [ ] **Step 2: Run the failing test**

Expected: FAIL because the helper and bounding box model do not exist yet.

- [ ] **Step 3: Add bounding box support to the view model**

Add a new simple DTO:

```csharp
public sealed record QuoteBoundingBoxDto(decimal X, decimal Y, decimal Z);
```

Add the property:

```csharp
public QuoteBoundingBoxDto? BoundingBox { get; set; }
```

Update `ToDraft()` to include the bounding box if the downstream DTO supports it. If the downstream DTO does not have a matching field, leave it as a client-only display field and do not force a wire contract change.

- [ ] **Step 4: Add metadata formatting helper**

Add:

```csharp
private string FormatUploadedPartMetadata(QuotePartViewModel part)
{
    var parts = new List<string>();

    if (part.FileSizeBytes > 0)
    {
        parts.Add(FormatFileSize(part.FileSizeBytes));
    }

    if (part.VolumeCc > 0)
    {
        parts.Add($"{part.VolumeCc:0.##} cc");
    }

    if (part.BoundingBox is not null)
    {
        parts.Add($"{part.BoundingBox.X:0.#} × {part.BoundingBox.Y:0.#} × {part.BoundingBox.Z:0.#} mm");
    }

    return string.Join(" · ", parts);
}
```

- [ ] **Step 5: Update the uploaded file list UI**

In the uploaded files section, render the metadata under the file name:

```razor
<div>
    <strong>@part.FileName</strong>
    <small>@UploadedPartStatus(part)</small>
    @if (QuoteUploadConstraints.IsSupportedCadFileName(part.FileName))
    {
        <small class="qe-agent-artifact-metadata">@FormatUploadedPartMetadata(part)</small>
    }
</div>
```

- [ ] **Step 6: Update the 3D viewer header to show metadata**

In the 3D viewer section header, add the metadata line under the filename:

```razor
<div>
    <span>@Text("3D viewer", "ตัวแสดง 3D")</span>
    <strong>@SelectedUploadedPart.FileName</strong>
    @if (QuoteUploadConstraints.IsSupportedCadFileName(SelectedUploadedPart.FileName))
    {
        <small class="qe-agent-artifact-viewer-metadata">@FormatUploadedPartMetadata(SelectedUploadedPart)</small>
    }
</div>
```

- [ ] **Step 7: Add CSS for metadata lines**

Add styles for `.qe-agent-artifact-metadata` and `.qe-agent-artifact-viewer-metadata` to keep the metadata compact and readable.

- [ ] **Step 8: Run the test again**

Expected: PASS.

---

### Task 4: Verify image input limitation handling

**Files:**
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\QuoteAgent\QuoteAgentLaunchShell.razor`
- Test: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\Maliev.QuoteEngine.Tests.csproj`

- [ ] **Step 1: Add a test for image error messaging**

Add a test that verifies the error message is shown when image input is not supported:

```csharp
var message = BuildImageInputUnsupportedMessage();

Assert.Contains("image input", message, StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 2: Run the failing test**

Expected: FAIL because the helper does not exist yet.

- [ ] **Step 3: Implement the message helper**

Add a helper method:

```csharp
private string BuildImageInputUnsupportedMessage()
{
    return Text(
        "Image input is not supported by this model yet. Please upload a 3D CAD file instead.",
        "โมเดลนี้ยังไม่รองรับการอ่านรูปภาพ กรุณาอัปโหลดไฟล์ CAD/3D แทน");
}
```

- [ ] **Step 4: Add a guard in the image upload path**

If there is already a handler that attempts to process image uploads, add a guard that displays the above message instead of proceeding with unsupported image analysis.

- [ ] **Step 5: Run the test again**

Expected: PASS.

---

### Task 5: Browser verification

**Files:**
- Modify: none

- [ ] **Step 1: Run the app locally**

Run:

```powershell
dotnet run --project Maliev.QuoteEngine.Bff\Maliev.QuoteEngine.Bff.csproj
```

- [ ] **Step 2: Verify in browser**

Check that:

1. The workbench artifact list no longer shows a duplicate "3D viewer - Ring1.stl" entry.
2. Clicking the `Ring1.stl` file tile opens the 3D viewer above.
3. Sending a message scrolls the chat to the latest message.
4. The floating scroll-to-bottom button appears when scrolled up and works when clicked.
5. 3D file entries show file size, bounding box, and volume when available.
6. Image upload attempts show a clear unsupported input message instead of a raw error.

---

### Task 6: Commit

- [ ] **Step 1: Review changed files**

Run:

```powershell
git status --short
git diff --stat
```

- [ ] **Step 2: Commit**

Commit only the files changed for this task:

```powershell
git add Maliev.QuoteEngine.Client\Components\QuoteAgent\QuoteAgentLaunchShell.razor Maliev.QuoteEngine.Client\wwwroot\js\maliev-chatbot.js Maliev.QuoteEngine.Client\wwwroot\css\app.css Maliev.QuoteEngine.Client\Models\QuotePartViewModel.cs Maliev.QuoteEngine.Tests\*.cs
git commit -m "fix: clean quote agent workbench artifacts"
```

---
