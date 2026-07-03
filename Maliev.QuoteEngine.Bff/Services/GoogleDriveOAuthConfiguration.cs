namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Reads the Google Drive OAuth client credentials shared by the connector controller and the agent service.
/// </summary>
internal static class GoogleDriveOAuthConfiguration
{
    public static string? ClientId(IConfiguration configuration)
    {
        return configuration["GoogleDrive:ClientId"] ?? configuration["Authentication:Google:ClientId"];
    }

    public static string? ClientSecret(IConfiguration configuration)
    {
        return configuration["GoogleDrive:ClientSecret"] ?? configuration["Authentication:Google:ClientSecret"];
    }

    public static string? PickerApiKey(IConfiguration configuration)
    {
        return configuration["GoogleDrive:PickerApiKey"] ?? configuration["GoogleDrive:DeveloperKey"];
    }

    public static string? PickerAppId(IConfiguration configuration)
    {
        return configuration["GoogleDrive:PickerAppId"] ??
            configuration["GoogleDrive:AppId"] ??
            configuration["GoogleDrive:ProjectNumber"];
    }

    public static bool IsConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(ClientId(configuration)) && !string.IsNullOrWhiteSpace(ClientSecret(configuration));
    }

    public static bool IsPickerConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(ClientId(configuration)) &&
            !string.IsNullOrWhiteSpace(PickerApiKey(configuration)) &&
            !string.IsNullOrWhiteSpace(PickerAppId(configuration));
    }
}
