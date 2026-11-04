namespace Portal.Configuration;

public record Configuration
{
    
    public static string Version => "0.2.1";
    public static string Message => string.Empty;
    public static string AppSettingsFilePath => "/opt/resources/appsettings.json";
    public static string NlogConfigPath => "/opt/resources/nlog.config";
}