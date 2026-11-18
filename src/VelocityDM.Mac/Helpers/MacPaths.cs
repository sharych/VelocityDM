namespace VelocityDM.Mac.Helpers;

public static class MacPaths
{
    public static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string AppSupportDir =>
        Path.Combine(HomeDirectory, "Library", "Application Support", "VelocityDM");

    public static string ExtensionDirectory =>
        Path.Combine(AppSupportDir, "ChromeExtension");

    public static string DefaultDownloadsDir =>
        Path.Combine(HomeDirectory, "Downloads", "VelocityDM");

    public static string LaunchAgentPlistPath =>
        Path.Combine(HomeDirectory, "Library", "LaunchAgents", "com.velocitydm.app.plist");
}
