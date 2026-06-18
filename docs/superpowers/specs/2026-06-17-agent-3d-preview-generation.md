# Agent-Generated 3D Preview Mockups

## Problem

The chatbot agent often rejects customer inquiries by redirecting to "please upload a 3D file" instead of analyzing what the customer actually provided — text descriptions, sketches, photos, drawings, or requirements. It has no ability to create a visual 3D mockup based on its understanding of the project.

## Solution

1. **Strengthen agent prompt** to never reject non-CAD input and always extract shape/dimensions from whatever is provided.
2. **Add a `quote_generate_3d_preview` tool** that accepts shape primitives (box, cylinder, sphere, cone) with inferred dimensions and generates a GLB file server-side via SharpGLTF.
3. **Add an inline BabylonJS 3D viewer** inside assistant chat messages so the customer can orbit/zoom/pan the generated preview and verify the agent's understanding.

---

## Components

### 1. Agent Prompt — `ComposeAgentMessage` (`QuoteAgentService.cs:3287`)

**Strengthen existing guidance:**

```
Guidance:
- NEVER respond by asking the customer to upload or send a 3D/CAD file as your first or only message. This is a rejection and must be avoided.
- Analyze text descriptions, photos, sketches, drawings, and reference images to extract shape, dimensions, material, process, and quantity.
- If you can infer enough shape and size information, call quote_generate_3d_preview to create a 3D preview GLB of the inferred part.
- Describe what you created, list your assumptions, and ask the customer to verify the shape and dimensions.
- Only mention CAD file uploads as an optional refinement step, never as a gate.
- Examples of inference: "rectangular 50x30mm" → box(50,30,5) with FDM PLA; "mounting holes" → add cylinder primitives for hole indicators.
```

### 2. New DTOs — `QuoteAgentDtos.cs`

```csharp
/// <summary>
/// Describes a primitive shape in a generated 3D preview model.
/// </summary>
public sealed class QuoteModelPrimitiveDto
{
    /// <summary>Shape type: box, cylinder, sphere, cone.</summary>
    public string ShapeType { get; set; } = "box";

    /// <summary>Size along X in mm (or diameter for cylinder/sphere).</summary>
    public double LengthX { get; set; }

    /// <summary>Size along Y in mm (or height for cylinder).</summary>
    public double LengthY { get; set; }

    /// <summary>Size along Z in mm (or depth).</summary>
    public double LengthZ { get; set; }

    /// <summary>Position offset from origin in mm.</summary>
    public double OffsetX { get; set; }

    /// <summary>Position offset from origin in mm.</summary>
    public double OffsetY { get; set; }

    /// <summary>Position offset from origin in mm.</summary>
    public double OffsetZ { get; set; }

    /// <summary>Optional: marks this as a hole indicator (contrasting color).</summary>
    public bool IsHoleIndicator { get; set; }
}

/// <summary>
/// Request to generate a 3D preview model from inferred primitives.
/// </summary>
public sealed class QuoteGenerateModelRequest
{
    /// <summary>Human-readable description of the part.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Inferred shape primitives composing the part.</summary>
    public List<QuoteModelPrimitiveDto> Primitives { get; set; } = [];

    /// <summary>Inferred manufacturing process hint (fdm, sla, cnc).</summary>
    public string? ProcessHint { get; set; }
}

/// <summary>
/// Result of generating a 3D preview model.
/// </summary>
public sealed class QuoteGenerateModelResponse
{
    /// <summary>The generated GLB viewer URL.</summary>
    public string GlbUrl { get; set; } = string.Empty;

    /// <summary>Artifact ID for the generated viewer.</summary>
    public Guid ArtifactId { get; set; }

    /// <summary>Part ID if a part placeholder was created.</summary>
    public Guid? PartId { get; set; }
}
```

### 3. New Tool: `quote_generate_3d_preview` — `QuoteAgentService.cs`

Registered in `ExecuteToolAsync` switch:

```csharp
"quote_generate_3d_preview" => Generate3DPreview(state, request.Arguments),
```

Handler flow:
1. Parse arguments (description, primitives array, process_hint)
2. Call `QuoteModelGeneratorService.GenerateAsync()`
3. Upsert a `QuotePartDraftDto` placeholder with the inferred process/material
4. Add a `viewer` artifact with the signed GLB URL
5. Upsert a `requirements_summary` artifact
6. Return success with the GLB URL

### 4. New Service: `QuoteModelGeneratorService`

**Interface:**

```csharp
public interface IQuoteModelGeneratorService
{
    Task<QuoteGenerateModelResponse> GenerateAsync(
        QuoteGenerateModelRequest request,
        CancellationToken cancellationToken);
}
```

**Implementation using SharpGLTF:**

```
1. Create a new glTF Scene
2. For each primitive:
   a. Build vertex buffer (positions + normals) for the shape type
   b. Build triangle index buffer
   c. Create mesh with a PBR material (main body = neutral gray #CCCCCC, holes = red #FF4444)
   d. Apply offset transform as node translation
3. Build scene graph root node containing all primitive mesh nodes
4. Export to binary GLB stream
5. Upload stream via QuoteUploadServiceClient → get storage path
6. Resolve signed download URL (60-min expiry)
7. Return QuoteGenerateModelResponse with URL and artifact ID
```

**Supported primitives:**

| Shape | Parameters | Mesh |
|-------|-----------|------|
| Box | length_x, length_y, length_z | 8 vertices, 12 triangles (6 faces, 2 per face) |
| Cylinder | diameter, height (segments=24) | Approximated as 24-sided prism |
| Sphere | diameter (segments=16) | UV sphere with 16x16 segments |
| Cone | diameter, height (segments=24) | 24-sided cone |

### 5. Inline Chat 3D Viewer

**New component:** `QeInlinePartViewer.razor`

```
@inject IJSRuntime JS

<div @ref="_container" class="qe-inline-viewer-container">
    <div class="qe-inline-viewer-toolbar">
        <button @onclick="ResetCamera">Reset</button>
    </div>
</div>
```

- Height: 280px, full width of message bubble
- Border-radius matches message bubble style
- Loading state: centered spinner while GLB loads
- Error state: "Could not load 3D preview" fallback

**New JS module:** `wwwroot/js/quote-inline-viewer.js`

```javascript
// Singleton shared BabylonJS engine
const engine = new BABYLON.Engine(canvas, true, { preserveDrawingBuffer: true });
const scenes = new Map();

export async function initInlineViewer(containerId, glbUrl) {
    const container = document.getElementById(containerId);
    const canvas = createCanvas(container);
    const scene = new BABYLON.Scene(engine);
    // Load GLB, setup arc-rotate camera, default lighting
    scenes.set(containerId, scene);
    engine.runRenderLoop(() => scenes.forEach(s => s.render()));
}

export function disposeInlineViewer(containerId) {
    const scene = scenes.get(containerId);
    scene?.dispose();
    scenes.delete(containerId);
}

export function resetInlineViewer(containerId) {
    const scene = scenes.get(containerId);
    // Reset arc-rotate camera to defaults
}
```

**Integration in `QuoteAgentLaunchShell.razor`:**

In the assistant message rendering section (after markdown content, around line 489), check for `message.InlineViewer` and render `QeInlinePartViewer`:

```razor
@if (message.InlineViewer is not null)
{
    <section class="qe-agent-inline-viewer">
        <QeInlinePartViewer GlbUrl="@message.InlineViewer.GlbUrl"
                            ViewerId="@message.InlineViewer.ArtifactId" />
    </section>
}
```

**Message model update:** Add `InlineViewer` property to `QuoteAgentMessageViewModel`:

```csharp
public sealed class InlineViewerInfo
{
    public string GlbUrl { get; set; } = string.Empty;
    public Guid ArtifactId { get; set; }
}

public InlineViewerInfo? InlineViewer { get; set; }
```

**Population logic:** After receiving a `QuoteAgentTurnResponse`, check `response.Artifacts` for viewer-type artifacts. The first viewer artifact whose metadata contains `"generated": "true"` becomes `message.InlineViewer`.

### 6. Session Store Updates

The `QuoteAgentSessionState` doesn't need structural changes — it already stores artifacts and parts. The generated part will be added as:
- A `QuotePartDraftDto` with:
  - `FileName` = "[Generated] {description}"
  - `ProcessId` from inferred process (default "fdm")
  - `MaterialId` from inferred material (default "pla-black")
  - `Status` = "ModelGenerated"
  - `ViewerGlbUrl` = signed URL
- A `viewer` artifact linked to the part

### 7. UI Filter Update

`QuoteAgentUiHelpers.IsVisibleArtifact` currently hides all `viewer` artifacts from the artifact drawer. This should remain as-is for uploaded CAD viewer artifacts. The inline viewer artifacts rendered in chat are separate — they're detected by the `QuoteAgentMessageViewModel.InlineViewer` property, not by the artifact drawer. The drawer should continue to hide viewer artifacts.

---

## Data Flow

```
Customer: "I need a bracket 50x30mm"
  │
  ▼
POST /quote/v1/agent/messages
  │
  ▼
BFF: ComposeAgentMessage() → sends to ChatbotService
  │
  ▼
ChatbotService LLM analyzes → calls quote_generate_3d_preview {
    description: "Bracket 50x30x5mm with two ∅5mm mounting holes",
    primitives: [
      { shape_type: "box", length_x: 50, length_y: 30, length_z: 5 },
      { shape_type: "cylinder", diameter: 5, height: 5, offset_x: -18, is_hole: true },
      { shape_type: "cylinder", diameter: 5, height: 5, offset_x: 18, is_hole: true }
    ],
    process_hint: "fdm"
  }
  │
  ▼
BFF: QuoteModelGeneratorService
  → Build meshes with SharpGLTF
  → Export GLB to stream
  → Upload via QuoteUploadServiceClient
  → Resolve signed URL
  → Create part placeholder + viewer artifact
  → Return QuoteGenerateModelResponse
  │
  ▼
BFF: QuoteAgentTurnResponse {
    assistantText: "I created a preview of your bracket...",
    artifacts: [ { type: "viewer", url: "https://.../model.glb" } ],
    ...
  }
  │
  ▼
Client: QuoteAgentLaunchShell.razor
  → Detect viewer artifacts → set message.InlineViewer
  → Render text + QeInlinePartViewer with GLB URL
  → Customer can orbit/zoom/pan
```

---

## Testing

### Unit Tests — `QuoteModelGeneratorTests.cs`

| Test | Description |
|------|-------------|
| `GenerateBox_ReturnsValidGlb` | Box primitive → valid GLB binary, has correct vertex count |
| `GenerateComposite_ReturnsMultiMeshGlb` | Box + cylinder → GLB with 2 mesh nodes |
| `Generate_WithHoleIndicator_AppliesDistinctMaterial` | Hole primitives have red material |
| `Generate_InvalidShape_ReturnsError` | Unknown shape type handled gracefully |
| `Generate_EmptyPrimitives_ReturnsError` | No primitives → validation failure |

### Integration Tests — `QuoteAgentEndpointTests.cs`

| Test | Description |
|------|-------------|
| `Generate3DPreview_Tool_ReturnsArtifact` | Tool handler returns valid artifact with URL |
| `Generate3DPreview_CreatesPartPlaceholder` | Part is created with ModelGenerated status |
| `Generate3DPreview_UpdatesGates` | Geometry gate passes after generation |

### UI Tests — `QuoteAgentUiHelperTests.cs`

| Test | Description |
|------|-------------|
| `InlineViewer_IsSeparateFromArtifactDrawer` | Viewer artifacts still hidden from drawer |

---

## Files Changed

| File | Change Type | Description |
|------|-------------|-------------|
| `Shared/Agent/QuoteAgentDtos.cs` | Modify | Add `QuoteModelPrimitiveDto`, `QuoteGenerateModelRequest`, `QuoteGenerateModelResponse` (~50 lines) |
| `Bff/Services/QuoteAgentService.cs` | Modify | Add `quote_generate_3d_preview` tool handler in `ExecuteToolAsync`; update `ComposeAgentMessage` prompt (+40 lines) |
| `Bff/Services/QuoteModelGeneratorService.cs` | **New** | `IQuoteModelGeneratorService` + implementation, SharpGLTF mesh generation (~250 lines) |
| `Bff/Services/QuoteAgentSessionStore.cs` | Modify | Add `IsModelGeneratedPart()` helper (optional, +10 lines) |
| `Client/Components/QuoteAgent/QeInlinePartViewer.razor` | **New** | Blazor component wrapping inline viewer JS (~80 lines) |
| `Client/wwwroot/js/quote-inline-viewer.js` | **New** | Shared BabylonJS engine, scene management, GLB loading (~180 lines) |
| `Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor` | Modify | Inline viewer rendering in assistant message section (~20 lines) |
| `Client/Models/QuoteAgentMessageViewModel.cs` | Modify | Add `InlineViewer` property (~5 lines) |
| `Tests/QuoteModelGeneratorTests.cs` | **New** | Unit tests for mesh generation (~120 lines) |
| `Tests/QuoteAgentEndpointTests.cs` | Modify | Add tool endpoint test (+30 lines) |
| `Tests/QuoteAgentUiHelperTests.cs` | Modify | Add visibility test (+10 lines) |

**NuGet dependency:**
- `SharpGLTF` (glTF/GLB toolkit via NuGet — uses `System.Numerics` built-in types)

---

## Out of Scope

- CSG (boolean subtract for real holes) — holes shown as contrasting-color indicators
- STEP export — GLB only for now
- Photo-to-3D ML service — agent manually extracts dimensions from images
- Full QePartViewer features (section, measure, DFM overlays) in inline mode
