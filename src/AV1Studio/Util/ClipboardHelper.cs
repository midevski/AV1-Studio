using System.Runtime.InteropServices;
using System.Windows;
using AV1Studio.Services;

namespace AV1Studio.Util;

/// <summary>
/// Clipboard access that tolerates other programs holding the clipboard (CLIPBRD_E_CANT_OPEN).
/// Retries asynchronously, so the UI never freezes, and never throws.
/// </summary>
public static class ClipboardHelper
{
    private const int Attempts = 6;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(120);

    /// <summary>Returns true when the text is on the clipboard. Must be called on the UI thread.</summary>
    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                // copy: true keeps the text available after AV1 Studio closes; this flush is what fails
                // while another application has the clipboard open, so it is retried.
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: attempt < Attempts);
                return true;
            }
            catch (Exception ex) when (ex is ExternalException or COMException or ThreadStateException)
            {
                if (attempt == Attempts)
                {
                    Log.Warn($"Clipboard unavailable: {ex.Message}");
                    return false;
                }
                await Task.Delay(Delay);
            }
        }
        return false;
    }
}
