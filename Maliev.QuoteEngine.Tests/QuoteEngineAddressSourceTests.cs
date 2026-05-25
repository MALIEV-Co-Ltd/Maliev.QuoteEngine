namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineAddressSourceTests
{
    [Fact]
    public void Profile_page_renders_customer_address_book_with_google_picker()
    {
        var profile = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Profile.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var picker = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "GoogleAddressPicker.razor");
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");

        Assert.Contains("GoogleAddressPicker", profile, StringComparison.Ordinal);
        Assert.Contains("Address No./Moo/Soi/Road", profile, StringComparison.Ordinal);
        Assert.Contains("Sub District", profile, StringComparison.Ordinal);
        Assert.Contains("Contact Information", profile, StringComparison.Ordinal);
        Assert.Contains("Note to driver", profile, StringComparison.Ordinal);
        Assert.Contains("Set as a default address", profile, StringComparison.Ordinal);
        Assert.Contains("ProvinceLocked", profile, StringComparison.Ordinal);
        Assert.Contains("PostalCodeLocked", profile, StringComparison.Ordinal);
        Assert.Contains("ApplyGoogleAddressSelection", profile, StringComparison.Ordinal);
        Assert.Contains("Thai address registry", profile, StringComparison.Ordinal);
        Assert.Contains("SearchRegistryLocationsAsync", profile, StringComparison.Ordinal);
        Assert.Contains("ApplyRegistryLocation", profile, StringComparison.Ordinal);
        Assert.Contains("AddressSource = \"RegistryThaiLocation\"", profile, StringComparison.Ordinal);
        Assert.Contains("RecipientName = _profile?.DisplayName", profile, StringComparison.Ordinal);
        Assert.Contains("RecipientPhone = _profile?.Phone", profile, StringComparison.Ordinal);

        Assert.Contains("quote/v1/account/addresses", apiClient, StringComparison.Ordinal);
        Assert.Contains("GetThaiAddressLocationsAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/address/thai-locations", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/address/google-config", picker, StringComparison.Ordinal);
        Assert.Contains("malievQuoteGoogleAddressPicker.initializeSearch", picker, StringComparison.Ordinal);
        Assert.Contains("js/quote-google-address-picker.js", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Address_bff_contract_forwards_customer_service_google_metadata()
    {
        var accountController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "AccountController.cs");
        var addressController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "AddressController.cs");
        var customerClient = ReadRepoFile("Maliev.QuoteEngine.Bff", "Clients", "CustomerServiceClient.cs");
        var registryClient = ReadRepoFile("Maliev.QuoteEngine.Bff", "Clients", "RegistryServiceClient.cs");
        var program = ReadRepoFile("Maliev.QuoteEngine.Bff", "Program.cs");
        var dto = ReadRepoFile("Maliev.QuoteEngine.Shared", "Account", "AccountDtos.cs");
        var googleDto = ReadRepoFile("Maliev.QuoteEngine.Shared", "Account", "GoogleAddressDtos.cs");

        Assert.Contains("[HttpGet(\"addresses\")]", accountController, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"addresses\")]", accountController, StringComparison.Ordinal);
        Assert.Contains("[HttpPatch(\"addresses/{addressId:guid}\")]", accountController, StringComparison.Ordinal);
        Assert.Contains("[HttpDelete(\"addresses/{addressId:guid}\")]", accountController, StringComparison.Ordinal);
        Assert.Contains("ownerType = \"Customer\"", accountController, StringComparison.Ordinal);
        Assert.Contains("placeLabel = request.PlaceLabel", accountController, StringComparison.Ordinal);
        Assert.Contains("driverNote = request.DriverNote", accountController, StringComparison.Ordinal);
        Assert.Contains("addressSource = string.IsNullOrWhiteSpace(request.AddressSource) ? \"Manual\" : request.AddressSource", accountController, StringComparison.Ordinal);
        Assert.Contains("googlePlaceId = request.GooglePlaceId", accountController, StringComparison.Ordinal);
        Assert.Contains("latitude = request.Latitude", accountController, StringComparison.Ordinal);
        Assert.Contains("CustomerOwnsAddressAsync", accountController, StringComparison.Ordinal);

        Assert.Contains("[Route(\"quote/v{version:apiVersion}/address\")]", addressController, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"google-config\")]", addressController, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"thai-locations\")]", addressController, StringComparison.Ordinal);
        Assert.Contains("SearchThaiLocationsAsync", addressController, StringComparison.Ordinal);
        Assert.Contains("GoogleMaps", addressController, StringComparison.Ordinal);

        Assert.Contains("/customer/v1/addresses?ownerType=Customer&ownerId=", customerClient, StringComparison.Ordinal);
        Assert.Contains("PostAsJsonAsync(\"/customer/v1/addresses\"", customerClient, StringComparison.Ordinal);
        Assert.Contains("PatchAsJsonAsync($\"/customer/v1/addresses/{addressId:D}\"", customerClient, StringComparison.Ordinal);
        Assert.Contains("/registry/v1/thai/addresses/autocomplete", registryClient, StringComparison.Ordinal);
        Assert.Contains("AddAuthenticatedServiceClient<IRegistryServiceClient, RegistryServiceClient>(\"RegistryService\")", program, StringComparison.Ordinal);

        Assert.Contains("public string? PlaceLabel", dto, StringComparison.Ordinal);
        Assert.Contains("public string? DriverNote", dto, StringComparison.Ordinal);
        Assert.Contains("public string AddressSource", dto, StringComparison.Ordinal);
        Assert.Contains("public string? GooglePlaceId", dto, StringComparison.Ordinal);
        Assert.Contains("public decimal? Latitude", dto, StringComparison.Ordinal);
        Assert.Contains("public decimal? Longitude", dto, StringComparison.Ordinal);
        Assert.Contains("ThaiAddressRegistryLocationDto", googleDto, StringComparison.Ordinal);
    }

    [Fact]
    public void Google_address_script_uses_places_widget_and_reverse_geocoding()
    {
        var script = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-google-address-picker.js");

        Assert.Contains("PlaceAutocompleteElement", script, StringComparison.Ordinal);
        Assert.Contains("gmp-select", script, StringComparison.Ordinal);
        Assert.Contains("place.fetchFields", script, StringComparison.Ordinal);
        Assert.Contains("includedRegionCodes", script, StringComparison.Ordinal);
        Assert.Contains("\"th\"", script, StringComparison.Ordinal);
        Assert.Contains("AdvancedMarkerElement", script, StringComparison.Ordinal);
        Assert.Contains("gmpDraggable: true", script, StringComparison.Ordinal);
        Assert.Contains("geocoder.geocode", script, StringComparison.Ordinal);
        Assert.Contains("GoogleMapPin", script, StringComparison.Ordinal);
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.Client")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Maliev.QuoteEngine repository root.");
    }
}
