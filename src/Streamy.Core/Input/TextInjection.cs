namespace Streamy.Core.Input;

/// <summary>
/// scrcpy-server types text through Android's virtual key character map, which only knows printable ASCII;
/// any other character (Arabic, accented letters, emoji) is dropped with "Could not inject char". Such text has
/// to reach the phone through its clipboard and be pasted instead.
/// </summary>
public static class TextInjection
{
    public static bool NeedsPaste(string text) => text.Any(c => c is < ' ' or > '~');
}
