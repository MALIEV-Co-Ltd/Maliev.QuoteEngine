namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineAddressSourceTests
{
    [Fact]
    public void Profile_page_shows_read_only_addresses_with_manage_link_to_web()
    {
        var profile = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Profile.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");

        // Address list is rendered read-only; editing is delegated to the Web account hub.
        Assert.Contains("quote/v1/account/addresses", apiClient, StringComparison.Ordinal);
        Assert.Contains("GetAddressesAsync", apiClient, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateAddressAsync", apiClient, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateAddressAsync", apiClient, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAddressAsync", apiClient, StringComparison.Ordinal);

        // Profile page shows addresses but no editing form.
        Assert.Contains("/account-hub", profile, StringComparison.Ordinal);
        Assert.Contains("Manage addresses", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAddressAsync", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAddressAsync", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("EditAddress(", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("GoogleAddressPicker", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchRegistryLocationsAsync", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Address_bff_contract_exposes_read_only_customer_addresses()
    {
        var accountController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "AccountController.cs");
        var addressController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "AddressController.cs");
        var customerClient = ReadRepoFile("Maliev.QuoteEngine.Bff", "Clients", "CustomerServiceClient.cs");
        var registryClient = ReadRepoFile("Maliev.QuoteEngine.Bff", "Clients", "RegistryServiceClient.cs");
        var program = ReadRepoFile("Maliev.QuoteEngine.Bff", "Program.cs");
        var dto = ReadRepoFile("Maliev.QuoteEngine.Shared", "Account", "AccountDtos.cs");
        var googleDto = ReadRepoFile("Maliev.QuoteEngine.Shared", "Account", "GoogleAddressDtos.cs");

        // Read-only address list endpoint is kept for profile display and checkout address picker.
        Assert.Contains("[HttpGet(\"addresses\")]", accountController, StringComparison.Ordinal);
        // Write endpoints removed — profile editing happens in Maliev.Web.
        Assert.DoesNotContain("[HttpPost(\"addresses\")]", accountController, StringComparison.Ordinal);
        Assert.DoesNotContain("[HttpPatch(\"addresses/{addressId:guid}\")]", accountController, StringComparison.Ordinal);
        Assert.DoesNotContain("[HttpDelete(\"addresses/{addressId:guid}\")]", accountController, StringComparison.Ordinal);

        // Thai address registry and Google picker endpoints remain (used by the quote/checkout flow).
        Assert.Contains("[Route(\"quote/v{version:apiVersion}/address\")]", addressController, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"google-config\")]", addressController, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"thai-locations\")]", addressController, StringComparison.Ordinal);
        Assert.Contains("SearchThaiLocationsAsync", addressController, StringComparison.Ordinal);
        Assert.Contains("GoogleMaps", addressController, StringComparison.Ordinal);

        Assert.Contains("/customer/v1/addresses?ownerType=Customer&ownerId=", customerClient, StringComparison.Ordinal);
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
