using System.IO;
using System.Security;
using Microsoft.Win32;
using PeriBattery.Core.Infra;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PeriBattery;

/// <summary>
/// Windows toast notifications for an unpackaged app. Toasts need an AppUserModelID known to the
/// shell: the installer's Start menu shortcut carries it, and for the portable build the same ID is
/// registered per-user under HKCU\Software\Classes\AppUserModelId (no admin, removed on uninstall).
/// </summary>
internal sealed class Toasts(ILog log)
{
    public const string AppUserModelId = "PeriBattery.App";
    private const string RegistryPath = @"Software\Classes\AppUserModelId\" + AppUserModelId;

    private ToastNotifier? _notifier;

    public void Register()
    {
        try
        {
            Native.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("DisplayName", "PeriBattery");

            // The shell needs the logo as an image file on disk.
            string logo = Path.Combine(SettingsStore.DataFolder, "app.png");
            if (!File.Exists(logo))
            {
                Directory.CreateDirectory(SettingsStore.DataFolder);
                using Stream? source = typeof(Toasts).Assembly.GetManifestResourceStream("PeriBattery.app.png");
                if (source != null)
                {
                    using FileStream target = File.Create(logo);
                    source.CopyTo(target);
                }
            }
            if (File.Exists(logo)) key.SetValue("IconUri", logo);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            log.Warn("toasts: could not register the AppUserModelID: " + ex.Message);
        }
    }

    public void Show(string title, string body)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text/><text/></binding></visual></toast>");
            XmlNodeList texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(body));

            _notifier ??= ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            _notifier.Show(new ToastNotification(xml));
            log.Info($"toast shown: {title} / {body}");
        }
        catch (Exception ex)
        {
            // Notifications disabled by policy, Focus Assist quirks, missing registration...
            log.Warn($"toasts: could not show \"{title}\": {ex.GetType().Name}: {ex.Message}");
        }
    }
}
