using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineSourceTests
{
    [Fact]
    public void QeDfmDtos_round_trip_through_json()
    {
        var report = new QeFdmDfmReport(
            ThinWallCount: 2,
            OverhangFaceCount: 5,
            OverhangAreaCm2: 3.14m,
            SupportRequired: true,
            SmallDetailCount: 0,
            Issues: [new QeDfmIssueItem("warning", "THIN_WALL", "Wall is thinner than 1.2mm")]);

        var json = JsonSerializer.Serialize(report);
        var deserialized = JsonSerializer.Deserialize<QeFdmDfmReport>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.ThinWallCount);
        Assert.Single(deserialized.Issues);
        Assert.Equal("THIN_WALL", deserialized.Issues[0].Code);
        Assert.Equal(5, deserialized.OverhangFaceCount);
        Assert.Equal(3.14m, deserialized.OverhangAreaCm2);
        Assert.True(deserialized.SupportRequired);
        Assert.Equal(0, deserialized.SmallDetailCount);
        Assert.Equal("warning", deserialized.Issues[0].Severity);
        Assert.Equal("Wall is thinner than 1.2mm", deserialized.Issues[0].Message);
    }

    [Fact]
    public void QeGlbReadyPayload_round_trip_through_json()
    {
        var payload = new QeGlbReadyPayload(
            StoragePath: "quotes/temp/sess/uid/part.step",
            GlbUrl: "https://cdn.example.com/part.glb?sig=abc",
            ThumbnailUrl: null,
            BodyCount: 1,
            IsManifold: true,
            Failed: false,
            ErrorCode: null);

        var json = JsonSerializer.Serialize(payload);
        var deserialized = JsonSerializer.Deserialize<QeGlbReadyPayload>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("quotes/temp/sess/uid/part.step", deserialized.StoragePath);
        Assert.False(deserialized.Failed);
        Assert.Equal("https://cdn.example.com/part.glb?sig=abc", deserialized.GlbUrl);
        Assert.Null(deserialized.ThumbnailUrl);
        Assert.Equal(1, deserialized.BodyCount);
        Assert.True(deserialized.IsManifold);
        Assert.Null(deserialized.ErrorCode);
    }

    [Fact]
    public void QeDfmAnalysisReadyPayload_round_trip_covers_partial_and_full_paths()
    {
        // Partial: only FDM report, SLA and CNC null, empty overlay list
        var partial = new QeDfmAnalysisReadyPayload(
            StoragePath: "quotes/temp/s/u/part.step",
            IsManifold: true,
            NonManifoldReason: null,
            FdmReport: new QeFdmDfmReport(1, 2, 1.5m, true, 0, [new QeDfmIssueItem("warning", "THIN_WALL", "Too thin")]),
            SlaReport: null,
            CncReport: null,
            OverlayGlbUrls: [],
            AnalysisErrorCode: null);

        var json = JsonSerializer.Serialize(partial);
        var d = JsonSerializer.Deserialize<QeDfmAnalysisReadyPayload>(json);

        Assert.NotNull(d);
        Assert.Equal("quotes/temp/s/u/part.step", d.StoragePath);
        Assert.True(d.IsManifold);
        Assert.NotNull(d.FdmReport);
        Assert.Equal(1, d.FdmReport.ThinWallCount);
        Assert.Equal(1.5m, d.FdmReport.OverhangAreaCm2);
        Assert.True(d.FdmReport.SupportRequired);
        Assert.Single(d.FdmReport.Issues);
        Assert.Equal("THIN_WALL", d.FdmReport.Issues[0].Code);
        Assert.Null(d.SlaReport);
        Assert.Null(d.CncReport);
        Assert.Empty(d.OverlayGlbUrls);

        // Full: all three reports, overlay URLs, non-null error context
        var full = new QeDfmAnalysisReadyPayload(
            StoragePath: "quotes/temp/s/u/housing.step",
            IsManifold: false,
            NonManifoldReason: "Open boundary edges detected",
            FdmReport: new QeFdmDfmReport(0, 0, 0m, false, 0, []),
            SlaReport: new QeSlaDfmReport(1, true, false, false, []),
            CncReport: new QeCncDfmReport(3, true, false, 0, false, false, false, []),
            OverlayGlbUrls: ["https://cdn.example.com/overlay1.glb", "https://cdn.example.com/overlay2.glb"],
            AnalysisErrorCode: null);

        var json2 = JsonSerializer.Serialize(full);
        var d2 = JsonSerializer.Deserialize<QeDfmAnalysisReadyPayload>(json2);

        Assert.NotNull(d2);
        Assert.False(d2.IsManifold);
        Assert.Equal("Open boundary edges detected", d2.NonManifoldReason);
        Assert.NotNull(d2.FdmReport);
        Assert.NotNull(d2.SlaReport);
        Assert.True(d2.SlaReport.ResinTrappingRisk);
        Assert.NotNull(d2.CncReport);
        Assert.Equal(3, d2.CncReport.SharpCornerCount);
        Assert.True(d2.CncReport.HasUndercuts);
        Assert.Equal(2, d2.OverlayGlbUrls.Count);
        Assert.Equal("https://cdn.example.com/overlay1.glb", d2.OverlayGlbUrls[0]);
    }

    [Fact]
    public void Quote_part_draft_customer_configuration_round_trips_through_json()
    {
        var draft = new QuotePartDraftDto
        {
            PartId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            FileId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            UploadId = "upload-projectnew-parity",
            FileName = "customer-fixture.step",
            ProcessId = "cnc",
            MaterialId = "al6061",
            FinishId = "cnc-bead-blast-clear",
            FinishCode = "BEAD_BLAST_CLEAR",
            ToleranceId = "iso-2768-m",
            ToleranceCode = "ISO2768_M",
            InspectionLevel = "Standard",
            RoughnessCode = "RA_1_6",
            Color = "Natural",
            HasThreadedHoles = true,
            ThreadSpecification = "M3x0.5",
            ThreadedHoleCount = 4,
            InsertType = "HeatSet",
            InsertCount = 2,
            BodyCount = 2,
            SelectedBodyIndex = 1,
            DrawingFiles =
            [
                new QuotePartAttachmentDto(
                    FileName: "customer-fixture-drawing.pdf",
                    StoragePath: "customers/c/quotes/q/drawing.pdf",
                    ContentType: "application/pdf",
                    FileSizeBytes: 123_456,
                    Kind: "Drawing")
            ],
            ViewerSettings = new QuotePartViewerSettingsDto(
                CameraPreset: "iso",
                EdgesEnabled: true,
                GridEnabled: false,
                DfmOverlayEnabled: true)
        };

        var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deserialized = JsonSerializer.Deserialize<QuotePartDraftDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(deserialized);
        Assert.Equal("cnc-bead-blast-clear", deserialized.FinishId);
        Assert.Equal("BEAD_BLAST_CLEAR", deserialized.FinishCode);
        Assert.Equal("iso-2768-m", deserialized.ToleranceId);
        Assert.Equal("ISO2768_M", deserialized.ToleranceCode);
        Assert.Equal("Standard", deserialized.InspectionLevel);
        Assert.Equal("RA_1_6", deserialized.RoughnessCode);
        Assert.Equal("Natural", deserialized.Color);
        Assert.True(deserialized.HasThreadedHoles);
        Assert.Equal("M3x0.5", deserialized.ThreadSpecification);
        Assert.Equal(4, deserialized.ThreadedHoleCount);
        Assert.Equal("HeatSet", deserialized.InsertType);
        Assert.Equal(2, deserialized.InsertCount);
        Assert.Equal(2, deserialized.BodyCount);
        Assert.Equal(1, deserialized.SelectedBodyIndex);
        Assert.Single(deserialized.DrawingFiles);
        Assert.Equal("Drawing", deserialized.DrawingFiles[0].Kind);
        Assert.Equal("iso", deserialized.ViewerSettings.CameraPreset);
        Assert.True(deserialized.ViewerSettings.EdgesEnabled);
        Assert.False(deserialized.ViewerSettings.GridEnabled);
        Assert.True(deserialized.ViewerSettings.DfmOverlayEnabled);
        Assert.Contains("\"finishId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectedBodyIndex\"", json, StringComparison.Ordinal);
        Assert.Contains("\"drawingFiles\"", json, StringComparison.Ordinal);
    }


    [Fact]
    public void Upload_script_matches_browser_files_by_name_and_size()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.Contains("file.name === mapping.fileName", source, StringComparison.Ordinal);
        Assert.Contains("file.size === mapping.fileSizeBytes", source, StringComparison.Ordinal);
        Assert.Contains("Content-Range", source, StringComparison.Ordinal);
        Assert.Contains("event.dataTransfer.files", source, StringComparison.Ordinal);
        Assert.Contains("registerDropzone", source, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void New_quote_workspace_supports_direct_upload_without_demo_card()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var script = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.Contains("class=\"qe-demo-sample-title\"", source, StringComparison.Ordinal);
        Assert.Contains("<img src=\"/images/logo.svg\" alt=\"MALIEV\" />", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Try the Quote Engine with a MALIEV sample file", source, StringComparison.Ordinal);
        Assert.Contains("Use sample file", source, StringComparison.Ordinal);
        Assert.Contains("LoadSampleFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("await LoadDemoProjectAsync();", source, StringComparison.Ordinal);
        Assert.Contains("private bool ShowDemoSampleCard => false;", source, StringComparison.Ordinal);
        Assert.Contains("ApplyDefaultRouting(part, candidate.FileName)", source, StringComparison.Ordinal);
        Assert.Contains("private bool IsWorkspaceReady => _parts.Count > 0;", source, StringComparison.Ordinal);
        Assert.Contains("qe-pn-root is-launch-screen", source, StringComparison.Ordinal);
        Assert.Contains("qe-pn-root is-workspace-ready", source, StringComparison.Ordinal);
        Assert.Contains("@if (IsWorkspaceReady)", source, StringComparison.Ordinal);
        Assert.Contains("RegisterActiveDropzoneAsync", source, StringComparison.Ordinal);
        Assert.Contains("quoteEngineUploads.unregisterDropzone", source, StringComparison.Ordinal);
        Assert.DoesNotContain("loadSampleFile", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(sampleUrl", script, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", script, StringComparison.Ordinal);
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        Assert.Contains(".qe-dropzone::before", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dropzone-icon", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dropzone-icon .mud-icon-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pdc-container {\n    position: relative;", styles, StringComparison.Ordinal);
        Assert.Contains("3D Model", ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor"), StringComparison.Ordinal);
        Assert.Contains(".qe-dfm-tab {\n    flex: 1 1 0;", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 380px;", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr));", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-launch-screen", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready .qe-plp-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready .qe-qsb-root", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-left", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-bottom", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-dropzone .mud-icon-root {", styles, StringComparison.Ordinal);
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "samples", "maliev-sample-bracket.step")));
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "models", "sample-bracket.glb")));
        Assert.False(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "sample-file.step")));
        Assert.Contains("\"Sign in to quote\"", source, StringComparison.Ordinal);
        Assert.Contains("Saved temporarily", source, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-qsb-customer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerBoundaryTitle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerBoundaryHint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"qe-zone-label\">Bill To</span>", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.SupportedCadAccept", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.SupportedCadExtensionLabel", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.MaxFileSizeMegabytes", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadHandoffRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Temporary upload storage until you sign in", source, StringComparison.Ordinal);
        Assert.DoesNotContain("max 10 GB per file", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign in before uploading customer-owned manufacturing files.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_and_quote_engine_boundaries_are_documented()
    {
        var readme = ReadRepoFile("README.md");

        Assert.Contains("Maliev.Web", readme, StringComparison.Ordinal);
        Assert.Contains("Maliev.QuoteEngine", readme, StringComparison.Ordinal);
        Assert.Contains("browser never supplies a trusted customer id", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Customer_auth_pages_follow_maliev_web_sign_in_pattern()
    {
        var signIn = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "SignIn.razor");
        var signUp = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "SignUp.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"auth-shell\"", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-title\"", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-title-logo\"", signIn, StringComparison.Ordinal);
        Assert.Contains("src=\"/images/logo.svg\"", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-google\"", signIn, StringComparison.Ordinal);
        Assert.Contains("href=\"@GoogleHref\"", signIn, StringComparison.Ordinal);
        Assert.Contains("private string GoogleHref =>", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeGoogleAsync", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-email-panel\"", signIn, StringComparison.Ordinal);
        Assert.Contains("Use email instead", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"eyebrow\">Customer account</span>", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"auth-divider\">or use email</div>", signIn, StringComparison.Ordinal);

        Assert.Contains("class=\"auth-shell\"", signUp, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-title-logo\"", signUp, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-google\"", signUp, StringComparison.Ordinal);
        Assert.Contains("href=\"@GoogleHref\"", signUp, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeGoogleAsync", signUp, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-email-panel\"", signUp, StringComparison.Ordinal);

        Assert.Contains(".auth-shell", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-title-logo", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-google", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-email-panel", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-field-help", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Customer_layout_uses_top_navigation_without_left_rail()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"quote-topbar\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Customer quote navigation\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"quote-brand\" href=\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"quote-brand-logo\"", layout, StringComparison.Ordinal);
        Assert.Contains("src=\"/images/logo.svg\"", layout, StringComparison.Ordinal);
        Assert.Contains("alt=\"MALIEV\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.maliev.com", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("quote-brand-mark", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Quote Engine", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"web-link\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(".web-link", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".quote-brand-mark", styles, StringComparison.Ordinal);
        Assert.Contains("justify-self: center;", styles, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--maliev-radius-tab);", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-topnav a.active", styles, StringComparison.Ordinal);
        Assert.Contains("background: var(--primary);", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-topnav a:focus-visible", styles, StringComparison.Ordinal);
        Assert.Contains("IsQuoteWorkspacePath", layout, StringComparison.Ordinal);
        Assert.Contains("\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("\"/quotes/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("@inject QuoteEngineApiClient Api", layout, StringComparison.Ordinal);
        Assert.Contains("@if (_authStatus.IsSignedIn)", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavLink href=\"/profile\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavLink href=\"/ndas\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavLink href=\"/documents\"", layout, StringComparison.Ordinal);
        Assert.Contains("<a class=\"text-button\" href=\"/auth/sign-in\">Sign in</a>", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"billing-account-menu\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"billing-account-trigger\" aria-label=\"Customer account and billing\"", layout, StringComparison.Ordinal);
        Assert.Contains("CustomerAvatarMarkup", layout, StringComparison.Ordinal);
        Assert.Contains("ProfileImageUrl", layout, StringComparison.Ordinal);
        Assert.Contains("AvatarInitials", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"customer-data-section\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/profile\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/ndas\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/documents\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/preferences\"", layout, StringComparison.Ordinal);
        Assert.Contains("ToggleThemeAsync", layout, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Toggle light or dark mode\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"quote-currency-select\"", layout, StringComparison.Ordinal);
        Assert.Contains("PersistCurrencyAsync", layout, StringComparison.Ordinal);
        Assert.Contains("<CascadingValue Value=\"_isDarkMode\" Name=\"IsDarkMode\">", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"billing-account-options\" role=\"listbox\" aria-label=\"Billing account\"", layout, StringComparison.Ordinal);
        Assert.Contains("Personal account", layout, StringComparison.Ordinal);
        Assert.Contains("Company account", layout, StringComparison.Ordinal);
        Assert.Contains("Manage account", layout, StringComparison.Ordinal);
        Assert.Contains("_profile = await Api.GetProfileAsync();", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Start quote", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"primary-button\" href=\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("_authStatus = await Api.GetAuthStatusAsync();", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"app-rail\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(".app-rail", styles, StringComparison.Ordinal);
        Assert.Contains(".customer-avatar", styles, StringComparison.Ordinal);
        Assert.Contains(".customer-data-section", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-theme-toggle", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-currency-select", styles, StringComparison.Ordinal);
        Assert.Contains(":root[data-maliev-theme=\"dark\"]", styles, StringComparison.Ordinal);
        Assert.Contains(".workspace--quote", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Currency_selectors_load_options_from_currency_service()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var preferences = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Preferences.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var program = ReadRepoFile("Maliev.QuoteEngine.Bff", "Program.cs");
        var currencyClient = ReadRepoFile("Maliev.QuoteEngine.Bff", "Clients", "CurrencyServiceClient.cs");
        var referenceController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "ReferenceDataController.cs");
        var referenceDtos = ReadRepoFile("Maliev.QuoteEngine.Shared", "ReferenceData", "ReferenceDataDtos.cs");

        Assert.DoesNotContain("SupportedCurrencies", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportedCurrencies", preferences, StringComparison.Ordinal);
        Assert.Contains("@foreach (var currency in _currencyOptions)", layout, StringComparison.Ordinal);
        Assert.Contains("@foreach (var currency in _currencyOptions)", preferences, StringComparison.Ordinal);
        Assert.Contains("await LoadCurrencyOptionsAsync()", layout, StringComparison.Ordinal);
        Assert.Contains("await LoadCurrencyOptionsAsync()", preferences, StringComparison.Ordinal);
        Assert.Contains("GetCurrenciesAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/reference-data/currencies", apiClient, StringComparison.Ordinal);
        Assert.Contains("AddAuthenticatedServiceClient<ICurrencyServiceClient, CurrencyServiceClient>(\"CurrencyService\")", program, StringComparison.Ordinal);
        Assert.Contains("/currency/v1/currencies?pageSize=1000&isActive=true", currencyClient, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"currencies\")]", referenceController, StringComparison.Ordinal);
        Assert.Contains("CurrencyOptionDto", referenceDtos, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_page_requires_signed_in_customer_session()
    {
        var profile = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Profile.razor");

        Assert.Contains("_authStatus = await Api.GetAuthStatusAsync();", profile, StringComparison.Ordinal);
        Assert.Contains("if (!_authStatus.IsSignedIn)", profile, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(\"/auth/sign-in?returnUrl=/profile\"", profile, StringComparison.Ordinal);
        Assert.Contains("_profile = await Api.GetProfileAsync();", profile, StringComparison.Ordinal);
        Assert.Contains("_ndas = [.. await Api.GetNdasAsync()];", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-overview-card\"", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-profile-card\"", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-action-grid\"", profile, StringComparison.Ordinal);
        Assert.Contains("ProfileImageUrl", profile, StringComparison.Ordinal);
        Assert.Contains("ProfileInitials", profile, StringComparison.Ordinal);
        Assert.Contains("NdaStatus", profile, StringComparison.Ordinal);
        Assert.Contains("NdaExpiryNotice", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/ndas\"", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/documents\"", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/orders\"", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/preferences\"", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_workspace_is_single_viewport_application_shell()
    {
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("height: 100dvh;", styles, StringComparison.Ordinal);
        Assert.Contains("overflow-x: clip;", styles, StringComparison.Ordinal);
        Assert.Contains(".workspace--quote {\n    flex: 1 1 0;\n    height: 100%;", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-workspace-host {\n    flex: 1 1 0;\n    height: 100%;", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("height: 100vh;", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-chat-backdrop", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-chat-drawer", styles, StringComparison.Ordinal);
        Assert.Contains("position: fixed;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-root", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: 99px;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-customer", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-lead {\n    flex: 1 1 auto;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-divider {\n    flex: 0 0 1px;\n    width: 1px;\n    background: var(--maliev-border);\n}", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-qsb-divider {\n    width: 1px;\n    box-shadow: var(--maliev-shadow-ring);\n}", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_workspace_uses_projectnew_parity_component_shell()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var detailCard = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("<QePartsListPanel", workspace, StringComparison.Ordinal);
        Assert.Contains("<QePartDetailCard", workspace, StringComparison.Ordinal);
        Assert.Contains("<QePartConfigSidebar", workspace, StringComparison.Ordinal);
        Assert.Contains("<QeQuoteSummaryBar", workspace, StringComparison.Ordinal);
        Assert.Contains("QeDrawingAttachmentsTab", detailCard, StringComparison.Ordinal);
        Assert.Contains("3D Model", detailCard, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"DFM Analysis\"", detailCard, StringComparison.Ordinal);
        Assert.Contains("qe-pn-mobile-toolbar", workspace, StringComparison.Ordinal);
        Assert.Contains("_isPartsDrawerOpen", workspace, StringComparison.Ordinal);
        Assert.Contains("_centerMode", workspace, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Quantity\"", ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor"), StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"qe-zone-label\">Bill To</span>", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerPicker", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("internal pricing override", workspace, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("flex: 0 0 270px;", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 380px;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-mobile-toolbar", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-parts-drawer", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1200px)", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Customer_assistant_drawer_is_integrated_into_quote_layout()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");
        var authComplete = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "AuthChatbotComplete.razor");

        Assert.Contains("CustomerAssistantDrawer", layout, StringComparison.Ordinal);
        Assert.Contains("topbar-chat-toggle", layout, StringComparison.Ordinal);
        Assert.Contains("Icon=\"@Icons.Material.Filled.SupportAgent\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon=\"@Icons.Material.Outlined.Chat\"", layout, StringComparison.Ordinal);
        Assert.Contains("quote-chat-backdrop", layout, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("MudDrawer", layout, StringComparison.Ordinal);
        Assert.Contains("chatbot-open", styles, StringComparison.Ordinal);
        Assert.Contains("js/maliev-chatbot.js", index, StringComparison.Ordinal);
        Assert.Contains("@page \"/auth/chatbot-complete\"", authComplete, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_loader_uses_maliev_logo_progress_and_status_contract()
    {
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var loaderScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-engine-loader.js");

        Assert.Contains("class=\"maliev-logo-loader\"", index, StringComparison.Ordinal);
        Assert.Contains("role=\"img\" aria-label=\"MALIEV\"", index, StringComparison.Ordinal);
        Assert.Contains("class=\"startup-progress\"", index, StringComparison.Ordinal);
        Assert.Contains("role=\"progressbar\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"startup-status\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"startup-logo\">MALIEV</div>", index, StringComparison.Ordinal);

        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "images", "logo.svg")));
        Assert.Contains("--wasm-logo-progress: 0%", styles, StringComparison.Ordinal);
        Assert.Contains("--logo-empty: var(--wasm-logo-empty, #d7dde6)", styles, StringComparison.Ordinal);
        Assert.Contains("--logo-progress: var(--wasm-logo-progress, 0%)", styles, StringComparison.Ordinal);
        Assert.Contains("mask: url('/images/logo.svg') center / contain no-repeat", styles, StringComparison.Ordinal);
        Assert.Contains(".maliev-logo-loader::before", styles, StringComparison.Ordinal);
        Assert.Contains("width: var(--logo-progress)", styles, StringComparison.Ordinal);
        Assert.Contains(".startup-progress span", styles, StringComparison.Ordinal);
        Assert.Contains("width: var(--wasm-loader-progress)", styles, StringComparison.Ordinal);

        Assert.Contains("startBlazor", loaderScript, StringComparison.Ordinal);
        Assert.Contains("loadBootResource", loaderScript, StringComparison.Ordinal);
        Assert.Contains("let displayedProgress = 0", loaderScript, StringComparison.Ordinal);
        Assert.Contains("Math.max(displayedProgress, progress)", loaderScript, StringComparison.Ordinal);
        Assert.Contains("markRuntimeReady", loaderScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusService_merge_not_overwrite_when_second_dfm_event_arrives()
    {
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        // First event: FDM report only
        var fdm = new QeFdmDfmReport(1, 0, 0m, false, 0, []);
        await svc.SetDfmReportsAsync(path, fdm, null, null, [], null, null);

        // Second event: CNC report only — FDM must survive
        var cnc = new QeCncDfmReport(2, false, true, 3, false, false, false, []);
        await svc.SetDfmReportsAsync(path, null, null, cnc, ["https://overlay1.glb"], null, null);

        var status = await svc.GetStatusAsync(path);
        Assert.NotNull(status);
        Assert.NotNull(status.FdmReport);           // preserved from first event
        Assert.Equal(1, status.FdmReport.ThinWallCount);
        Assert.NotNull(status.CncReport);           // added by second event
        Assert.Equal(3, status.CncReport.DrillHoleCount);
        Assert.Single(status.OverlayGlbUrls);       // accumulated
    }

    [Fact]
    public async Task StatusService_SetGlbReady_transitions_status()
    {
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        await svc.SetProcessingAsync(path);
        var processing = await svc.GetStatusAsync(path);
        Assert.Equal("Processing", processing!.Status);

        await svc.SetGlbReadyAsync(path, "https://glb.example.com/part.glb", null, 1, true);
        var ready = await svc.GetStatusAsync(path);
        Assert.Equal("GlbReady", ready!.Status);
        Assert.Equal("https://glb.example.com/part.glb", ready.GlbUrl);
        Assert.Equal(1, ready.BodyCount);
        Assert.True(ready.IsManifold);
    }

    [Fact]
    public async Task StatusService_SetFailed_sets_error_code()
    {
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        await svc.SetProcessingAsync(path);
        await svc.SetFailedAsync(path, "GlbSigningFailed");

        var status = await svc.GetStatusAsync(path);
        Assert.Equal("Failed", status!.Status);
        Assert.Equal("GlbSigningFailed", status.AnalysisErrorCode);
    }

    [Fact]
    public async Task StatusService_SetDfmReports_cold_insert_path_works_when_no_prior_entry()
    {
        // Scenario: DfmAnalysisReady event arrives before GlbReady (out-of-order delivery)
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        // No SetProcessingAsync or SetGlbReadyAsync called first
        var fdm = new QeFdmDfmReport(0, 0, 0m, false, 0, []);
        var cnc = new QeCncDfmReport(1, false, false, 0, false, false, true, []);
        await svc.SetDfmReportsAsync(path, fdm, null, cnc,
            ["https://overlay1.glb"], null, null);

        var status = await svc.GetStatusAsync(path);
        Assert.NotNull(status);
        Assert.Equal("DfmAnalysisReady", status.Status);
        Assert.NotNull(status.FdmReport);
        Assert.Equal(0, status.FdmReport.ThinWallCount);
        Assert.NotNull(status.CncReport);
        Assert.True(status.CncReport.IsTurnable);
        Assert.Null(status.SlaReport);
        Assert.Single(status.OverlayGlbUrls);
        // GlbUrl not set (no GlbReady event) — must be null, not throw
        Assert.Null(status.GlbUrl);
    }

    [Fact]
    public async Task FileAnalyzedConsumer_success_path_updates_status_and_pushes_GlbReady()
    {
        // Arrange
        var statusSvc = new QuoteFileAnalysisStatusService();
        await statusSvc.SetProcessingAsync("quotes/temp/s/u/bracket.step");

        var uploadClient = new FakeQuoteUploadServiceClient(
            glbUrl: "https://cdn.example.com/part.glb");

        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);

        var consumer = new QuoteFileAnalyzedConsumer(
            statusSvc, uploadClient, hubCtx,
            NullLogger<QuoteFileAnalyzedConsumer>.Instance);

        var @event = BuildFileAnalyzedEvent(
            storagePath: "quotes/temp/s/u/bracket.step",
            glbStoragePath: "processed/u/bracket.glb",
            thumbnailStoragePath: null,
            bodyCount: 1,
            isManifold: true,
            volumeCm3: 12.4);

        var consumeCtx = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        // Act
        await consumer.Consume(consumeCtx);

        // Assert — status updated
        var stored = await statusSvc.GetStatusAsync("quotes/temp/s/u/bracket.step");
        Assert.NotNull(stored);
        Assert.Equal("GlbReady", stored.Status);
        Assert.Equal("https://cdn.example.com/part.glb", stored.GlbUrl);
        Assert.Null(stored.ThumbnailUrl);
        Assert.Equal(1, stored.BodyCount);
        Assert.True(stored.IsManifold);

        // Assert — SignalR hub called with correct GlbReady payload
        var calls = hubGroup.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SendCoreAsync")
            .ToList();
        Assert.Single(calls);
        var args = calls[0].GetArguments();
        Assert.Equal("GlbReady", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var glbPayload = Assert.IsType<QeGlbReadyPayload>(payloadArgs[0]);
        Assert.Equal("quotes/temp/s/u/bracket.step", glbPayload.StoragePath);
        Assert.Equal("https://cdn.example.com/part.glb", glbPayload.GlbUrl);
        Assert.Null(glbPayload.ThumbnailUrl);
        Assert.False(glbPayload.Failed);
        Assert.Null(glbPayload.ErrorCode);
        Assert.Equal(1, glbPayload.BodyCount);
        Assert.True(glbPayload.IsManifold);
    }

    [Fact]
    public async Task FileAnalyzedConsumer_url_signing_failure_marks_failed_and_still_pushes_GlbReady()
    {
        var statusSvc = new QuoteFileAnalysisStatusService();
        var uploadClient = new ThrowingQuoteUploadServiceClient();

        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);

        var consumer = new QuoteFileAnalyzedConsumer(
            statusSvc, uploadClient, hubCtx,
            NullLogger<QuoteFileAnalyzedConsumer>.Instance);

        var @event = BuildFileAnalyzedEvent("quotes/s/u/p.step", "processed/p.glb", null, 1, true, 5.0);

        var consumeCtx = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        await consumer.Consume(consumeCtx);

        var stored = await statusSvc.GetStatusAsync("quotes/s/u/p.step");
        Assert.Equal("Failed", stored!.Status);
        Assert.Equal("GlbSigningFailed", stored.AnalysisErrorCode);

        // Assert — hub still called with Failed=true payload
        var calls = hubGroup.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SendCoreAsync")
            .ToList();
        Assert.Single(calls);
        var args = calls[0].GetArguments();
        Assert.Equal("GlbReady", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var glbPayload = Assert.IsType<QeGlbReadyPayload>(payloadArgs[0]);
        Assert.True(glbPayload.Failed);
        Assert.Equal("GlbSigningFailed", glbPayload.ErrorCode);
        Assert.Equal("", glbPayload.GlbUrl);  // empty string per contract when Failed=true
    }

    [Fact]
    public async Task DfmAnalysisReadyConsumer_merges_fdm_then_cnc_and_pushes_DfmAnalysisReady()
    {
        // Arrange: status service with an existing GlbReady entry
        var statusSvc = new QuoteFileAnalysisStatusService();
        const string storagePath = "quotes/temp/s/u/bracket.step";
        await statusSvc.SetGlbReadyAsync(storagePath, "https://glb.example.com/part.glb", null, 1, true);

        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);

        var uploadClient = new FakeQuoteUploadServiceClient("https://cdn.example.com/overlay.glb");
        var consumer = new QuoteDfmAnalysisReadyConsumer(
            statusSvc, uploadClient, hubCtx,
            NullLogger<QuoteDfmAnalysisReadyConsumer>.Instance);

        // First event: FDM report only
        var fdmPayload = new FdmDfmReportPayload
        {
            ThinWallCount = 2,
            SupportRequired = true,
            Issues = Array.Empty<FdmDfmReportPayloadIssuesItem>()
        };
        var firstEvent = new DfmAnalysisReadyEvent
        {
            Payload = new DfmAnalysisReadyEventPayload
            {
                StoragePath = storagePath,
                FdmReport = fdmPayload,
                OverlayPaths = new[] { "processed/u/overlay-fdm.glb" }
            }
        };
        var firstCtx = Substitute.For<ConsumeContext<DfmAnalysisReadyEvent>>();
        firstCtx.Message.Returns(firstEvent);
        firstCtx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(firstCtx);

        // Second event: CNC report only — FDM report must survive (merge-not-overwrite)
        var cncPayload = new CncDfmReportPayload
        {
            SharpCornerCount = 3,
            HasUndercuts = false,
            Issues = Array.Empty<CncDfmReportPayloadIssuesItem>()
        };
        var secondEvent = new DfmAnalysisReadyEvent
        {
            Payload = new DfmAnalysisReadyEventPayload
            {
                StoragePath = storagePath,
                CncReport = cncPayload,
                OverlayPaths = Array.Empty<string>()
            }
        };
        var secondCtx = Substitute.For<ConsumeContext<DfmAnalysisReadyEvent>>();
        secondCtx.Message.Returns(secondEvent);
        secondCtx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(secondCtx);

        // Assert — merged status: FDM survived, CNC added
        var stored = await statusSvc.GetStatusAsync(storagePath);
        Assert.NotNull(stored!.FdmReport);                             // from first event
        Assert.Equal(2, stored.FdmReport.ThinWallCount);
        Assert.True(stored.FdmReport.SupportRequired);
        Assert.NotNull(stored.CncReport);                              // from second event
        Assert.Equal(3, stored.CncReport.SharpCornerCount);
        Assert.Null(stored.SlaReport);
        Assert.Single(stored.OverlayGlbUrls);                         // signed URL accumulated

        // Assert — hub called once per Consume call = twice total
        var calls = hubGroup.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SendCoreAsync")
            .ToList();
        Assert.Equal(2, calls.Count);

        // Second call should carry the merged payload (FDM + CNC both present)
        var secondCallArgs = calls[1].GetArguments();
        Assert.Equal("DfmAnalysisReady", secondCallArgs[0]);
        var payloadArgs = (object?[])secondCallArgs[1]!;
        var dfmPayload = Assert.IsType<QeDfmAnalysisReadyPayload>(payloadArgs[0]);
        Assert.Equal(storagePath, dfmPayload.StoragePath);
        Assert.NotNull(dfmPayload.FdmReport);
        Assert.Equal(2, dfmPayload.FdmReport.ThinWallCount);
        Assert.NotNull(dfmPayload.CncReport);
        Assert.Equal(3, dfmPayload.CncReport.SharpCornerCount);
    }

    [Fact]
    public void QuoteWorkspaceRazor_registers_GlbReady_and_DfmAnalysisReady_handlers()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var detail = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        Assert.Contains("\"GlbReady\"", src);
        Assert.Contains("\"DfmAnalysisReady\"", src);
        Assert.Contains("FindPartByStoragePath", src);
        Assert.Contains("QePartViewer", detail);
        Assert.Contains("QeDfmTab", detail);
        Assert.Contains("QeBulkTable", detail);
    }

    [Fact]
    public void QuoteWorkspaceRazor_resets_demo_workspace_when_leaving_demo_route()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");

        Assert.Contains("SynchronizeRouteStateAsync", src, StringComparison.Ordinal);
        Assert.Contains("HasDemoWorkspace", src, StringComparison.Ordinal);
        Assert.Contains("ResetWorkspaceState", src, StringComparison.Ordinal);
        Assert.Contains("ShowDemoSampleCard => false", src, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(\"/demo\")", src, StringComparison.Ordinal);
    }

    [Fact]
    public void QeBulkTableRazor_exists_and_has_bulk_edit_columns()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeBulkTable.razor");
        Assert.Contains("Select process", src);
        Assert.Contains("Select material", src);
        Assert.DoesNotContain("<option value=\"\">BulkProcess", src);
        Assert.DoesNotContain("<option value=\"\">BulkMaterial", src);
        Assert.Contains("LineTotal", src);
        Assert.Contains("DfmReport", src);  // DFM indicator column
    }

    [Fact]
    public void Quote_workspace_scopes_ProjectNew_density_tokens()
    {
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains(".qe-pn-root {\n    --maliev-font-nano: 10px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-micro: 11px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-small: 12px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-base: 13px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-body: 13px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-large: 14px;", styles, StringComparison.Ordinal);
        Assert.Contains("--mud-typography-body1-size: 13px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-track-display: 0;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root .mud-input,", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_workspace_matches_ProjectNew_tablet_layout_contract()
    {
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("@media (max-width: 1200px)", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready {\n        height: auto;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pdc-thumb-area {\n        position: sticky;", styles, StringComparison.Ordinal);
        Assert.Contains("height: 45dvh;", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: 520px;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-sidebar {\n        flex: 0 0 auto;", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: none;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-root {\n        position: sticky;", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_card_controls_not_native_selects()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("Surface Finish", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-fin-card", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-tol-card", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-choice-card", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-feature-card", src, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"DFM reviewed\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-notes-input\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-bulk-badge\"", src, StringComparison.Ordinal);
        Assert.Contains("SetPartNotes", src, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", src, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(".qe-pcs-fin-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-tol-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-choice-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-notes-input", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeDetailCard_retains_viewer_and_exposes_body_tree_and_dfm_overlay()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("QePartViewer @ref", src, StringComparison.Ordinal);
        Assert.Contains("style=\"@(CenterMode == \"model\" ? \"\" : \"display:none\")\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pdc-body-tree", src, StringComparison.Ordinal);
        Assert.Contains("QeDfmOverlayPanel", src, StringComparison.Ordinal);
        Assert.Contains("OnRequestFreshViewerUrl", src, StringComparison.Ordinal);
        Assert.Contains("SelectBodyAsync", src, StringComparison.Ordinal);
        Assert.Contains("ToggleDfmOverlayAsync", src, StringComparison.Ordinal);
        Assert.Contains("NotifyVisibleAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("SelectBodyAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleDfmOverlayAsync", viewer, StringComparison.Ordinal);
        Assert.Contains(".qe-pdc-body-tree", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dfm-overlay", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeBulkTable_uses_ProjectNew_dense_table_shell()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeBulkTable.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"qe-pbt-root\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-header\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-bulk-panel\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-table-surface\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-input", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-configurator-button\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-card-stack\"", src, StringComparison.Ordinal);
        Assert.Contains("Threaded holes", src, StringComparison.Ordinal);
        Assert.Contains("Inserts", src, StringComparison.Ordinal);
        Assert.Contains("Notes", src, StringComparison.Ordinal);

        Assert.Contains(".qe-pbt-bulk-panel", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pbt-input", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pbt-card-stack", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeDrawingAttachmentsTab_uses_ProjectNew_two_column_surface()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeDrawingAttachmentsTab.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"qe-dat-root\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-dat-list-col\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-dat-preview-col\"", src, StringComparison.Ordinal);
        Assert.Contains("InputFile", src, StringComparison.Ordinal);
        Assert.Contains("SelectDrawing", src, StringComparison.Ordinal);
        Assert.Contains("RemoveDrawing", src, StringComparison.Ordinal);
        Assert.DoesNotContain("AddPlaceholderDrawing", src, StringComparison.Ordinal);
        Assert.DoesNotContain("draft://quote-engine", src, StringComparison.Ordinal);

        Assert.Contains(".qe-dat-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dat-preview-iframe", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_summary_bar_exposes_customer_quote_approval_order_and_payment_actions()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeQuoteSummaryBar.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");

        Assert.Contains("class=\"qe-qsb-details-popover\"", src, StringComparison.Ordinal);
        Assert.Contains("Approve quote", src, StringComparison.Ordinal);
        Assert.Contains("Create order", src, StringComparison.Ordinal);
        Assert.Contains("Pay now", src, StringComparison.Ordinal);
        Assert.Contains("Customer documents", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-qsb-zone qe-qsb-customer\"", src, StringComparison.Ordinal);
        Assert.Contains("BillingName", src, StringComparison.Ordinal);
        Assert.Contains("BillingHint", src, StringComparison.Ordinal);
        Assert.Contains("OnApproveQuoteRequested", src, StringComparison.Ordinal);
        Assert.Contains("OnPaymentRequested", src, StringComparison.Ordinal);

        Assert.Contains("BillingName=\"@BillingSummaryName\"", workspace, StringComparison.Ordinal);
        Assert.Contains("BillingHint=\"@BillingSummaryHint\"", workspace, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("InitiatePaymentAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("InitiatePaymentAsync", apiClient, StringComparison.Ordinal);
    }

    [Fact]
    public void QeDfmTabRazor_exists_and_references_all_three_report_types()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeDfmTab.razor");
        Assert.Contains("QeFdmDfmReport", src);
        Assert.Contains("QeSlaDfmReport", src);
        Assert.Contains("QeCncDfmReport", src);
        Assert.Contains("IsManifold", src);
    }

    [Fact]
    public void Quote_viewer_and_part_thumbnail_have_customer_safe_fallbacks()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var partsList = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartsListPanel.razor");

        Assert.Contains("DotNetObjectReference<QePartViewer>", viewer, StringComparison.Ordinal);
        Assert.Contains("OnLoadError", viewer, StringComparison.Ordinal);
        Assert.Contains("qe-viewer-fallback", viewer, StringComparison.Ordinal);
        Assert.Contains("/images/generated/sample-part.svg", viewer, StringComparison.Ordinal);
        Assert.Contains("onerror=", partsList, StringComparison.Ordinal);
        Assert.Contains("FallbackPartImage", partsList, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_viewer_exposes_ProjectNew_cad_toolbar_controls()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("SetRenderModeAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleGridAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleBoundingBoxAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleMeasureAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleThicknessAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleSectionAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("SetSectionAxisAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("SetProjectionAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("RotateModelAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("NotifyMeasureResult", viewer, StringComparison.Ordinal);
        Assert.Contains("new(\"left\", \"Left view\")", viewer, StringComparison.Ordinal);
        Assert.Contains("new(\"back\", \"Back view\")", viewer, StringComparison.Ordinal);
        Assert.Contains("new(\"bottom\", \"Bottom view\")", viewer, StringComparison.Ordinal);
        Assert.Contains("\"setRenderMode\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"setCameraProjection\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"rotateModel\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"showGrid\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"hideGrid\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"toggleBoundingBox\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"enableMeasureTool\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"enableThicknessAnalysis\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"setSectionPlane\"", viewer, StringComparison.Ordinal);
        Assert.Contains("qe-viewer-loading-orbit", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Solid shaded view\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Transparent view\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Orthographic camera\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Rotate model 90 degrees\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Toggle section cut\"", viewer, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Filled.FlipCameraIos", viewer, StringComparison.Ordinal);
        Assert.Contains(".qe-viewer-loading-orbit", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-vt-subtools", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_parts_panel_and_workspace_expose_duplicate_project_flow()
    {
        var partsList = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartsListPanel.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");

        Assert.Contains("[Parameter] public EventCallback OnDuplicateProject", partsList, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OnDuplicateProject\"", partsList, StringComparison.Ordinal);
        Assert.Contains("OnDuplicateProject=\"DuplicateProjectAsync\"", workspace, StringComparison.Ordinal);
        Assert.Contains("private async Task DuplicateProjectAsync()", workspace, StringComparison.Ordinal);
        Assert.Contains("DuplicateProjectAsync(_draftProjectId.Value", workspace, StringComparison.Ordinal);
        Assert.Contains("CreateDraftProjectAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("ApplyDuplicatedProject", workspace, StringComparison.Ordinal);
        Assert.Contains("DuplicateDraftProjectRequest", apiClient, StringComparison.Ordinal);
        Assert.Contains("DuplicateDraftProjectResponse", apiClient, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_has_correct_window_handle_and_no_internal_tools()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js");

        // Customer-facing debug handle
        Assert.Contains("window.quotePartViewer", js);

        // Internal tools must not be in the exported debug handle
        // (they may still exist as functions — just not exported via the handle)
        var handleBlock = ExtractWindowHandleBlock(js, "quotePartViewer");
        Assert.DoesNotContain("enableMeasureTool", handleBlock);
        Assert.DoesNotContain("enableThicknessAnalysis", handleBlock);
        Assert.DoesNotContain("enableBodyPicking", handleBlock);

        // Must have the customer-facing exports
        Assert.Contains("initialize", handleBlock);
        Assert.Contains("toggleDfmOverlay", handleBlock);
        Assert.Contains("setCameraPreset", handleBlock);
    }

    private static string ExtractWindowHandleBlock(string js, string handleName)
    {
        var start = js.IndexOf($"window.{handleName}", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = js.IndexOf("};", start, StringComparison.Ordinal);
        return end > start ? js[start..end] : js[start..];
    }

    private static FileAnalyzedEvent BuildFileAnalyzedEvent(
        string storagePath, string glbStoragePath, string? thumbnailStoragePath,
        int bodyCount, bool isManifold, double volumeCm3)
    {
        return new FileAnalyzedEvent
        {
            Payload = new FileAnalyzedEventPayload
            {
                StoragePath = storagePath,
                GlbStoragePath = glbStoragePath,
                ThumbnailStoragePath = thumbnailStoragePath,
                BodyCount = bodyCount,
                Metrics = new FileAnalyzedEventPayloadMetrics
                {
                    IsManifold = isManifold,
                    VolumeCm3 = volumeCm3,
                    SurfaceAreaCm2 = 0.0
                }
            }
        };
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        return File.ReadAllText(RepoPath(pathParts));
    }

    private static string RepoPath(params string[] pathParts)
    {
        var root = FindRepoRoot();
        return Path.Combine([root, .. pathParts]);
    }

    private static string FindRepoRoot()
    {
        var startDirectories = new List<string>();
        var configuredRoot = Environment.GetEnvironmentVariable("MALIEV_QUOTEENGINE_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            startDirectories.Add(configuredRoot);
        }

        startDirectories.Add(GetSourceDirectory());
        startDirectories.Add(AppContext.BaseDirectory);
        startDirectories.Add(Directory.GetCurrentDirectory());

        foreach (var startDirectory in startDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx"))
                    && Directory.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.Client")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Maliev.QuoteEngine repository root.");
    }

    private static string GetSourceDirectory([CallerFilePath] string sourceFile = "") => Path.GetDirectoryName(sourceFile) ?? Directory.GetCurrentDirectory();
}

// ── Test helpers for consumer tests ─────────────────────────────────────────

file sealed class FakeQuoteUploadServiceClient(string glbUrl)
    : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
{
    // Returns glbUrl for any storage path. If testing both GLB + thumbnail URLs,
    // use a subclass that distinguishes by path suffix.
    public override Task<string> GetDownloadUrlByPathAsync(string storagePath,
        int expirationMinutes = 60, CancellationToken ct = default)
        => Task.FromResult(glbUrl);
}

file sealed class ThrowingQuoteUploadServiceClient()
    : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
{
    public override Task<string> GetDownloadUrlByPathAsync(string storagePath,
        int expirationMinutes = 60, CancellationToken ct = default)
        => throw new HttpRequestException("Simulated signing failure");
}
