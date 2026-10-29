using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Lyricod.Platform;

static partial class InstalledFonts
{
    public static IReadOnlyList<string> Read(string language)
    {
        language = language == "zh-Hans" ? "zh-cn" : string.IsNullOrEmpty(language) ? CultureInfo.CurrentUICulture.Name : language;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IDWriteFactory? factory = null;
        IDWriteFontCollection? collection = null;
        try
        {
            var iid = typeof(IDWriteFactory).GUID;
            AudioNative.Check(DWriteCreateFactory(0, in iid, out var pointer));
            factory = AudioNative.Wrap<IDWriteFactory>(pointer);
            AudioNative.Check(factory.GetSystemFontCollection(out pointer, 0));
            collection = AudioNative.Wrap<IDWriteFontCollection>(pointer);
            uint count = collection.GetFontFamilyCount();
            for (uint i = 0; i < count; i++)
            {
                IDWriteFontFamily? family = null;
                IDWriteLocalizedStrings? strings = null;
                try
                {
                    AudioNative.Check(collection.GetFontFamily(i, out pointer));
                    family = AudioNative.Wrap<IDWriteFontFamily>(pointer);
                    AudioNative.Check(family.GetFamilyNames(out pointer));
                    strings = AudioNative.Wrap<IDWriteLocalizedStrings>(pointer);
                    if (strings.GetCount() == 0) continue;
                    AudioNative.Check(strings.FindLocaleName(language, out uint index, out int exists));
                    if (exists == 0) AudioNative.Check(strings.FindLocaleName("en-us", out index, out exists));
                    if (exists == 0) index = 0;
                    AudioNative.Check(strings.GetStringLength(index, out uint length));
                    if (length > 32768) continue;
                    unsafe
                    {
                        var buffer = new char[length + 1];
                        fixed (char* text = buffer) AudioNative.Check(strings.GetString(index, text, length + 1));
                        names.Add(new string(buffer, 0, (int)length));
                    }
                }
                finally { AudioNative.Release(strings); AudioNative.Release(family); }
            }
        }
        finally { AudioNative.Release(collection); AudioNative.Release(factory); }
        return names.OrderBy(n => n, StringComparer.Create(CultureInfo.GetCultureInfo(language), true)).ToArray();
    }

    [LibraryImport("dwrite.dll")]
    private static partial int DWriteCreateFactory(int factoryType, in Guid iid, out nint factory);
}

// Only the vtable prefix used here is declared. FontFamily includes FontList's three slots.
[GeneratedComInterface, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48")]
internal partial interface IDWriteFactory
{
    [PreserveSig] int GetSystemFontCollection(out nint collection, int checkForUpdates);
}

[GeneratedComInterface, Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc")]
internal partial interface IDWriteFontCollection
{
    [PreserveSig] uint GetFontFamilyCount();
    [PreserveSig] int GetFontFamily(uint index, out nint family);
}

[GeneratedComInterface, Guid("da20d8ef-812a-4c43-9802-62ec4abd7add")]
internal partial interface IDWriteFontFamily
{
    [PreserveSig] int GetFontCollection(out nint collection);
    [PreserveSig] uint GetFontCount();
    [PreserveSig] int GetFont(uint index, out nint font);
    [PreserveSig] int GetFamilyNames(out nint names);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("08256209-099a-4b34-b86d-c22b110e7771")]
internal unsafe partial interface IDWriteLocalizedStrings
{
    [PreserveSig] uint GetCount();
    [PreserveSig] int FindLocaleName(string locale, out uint index, out int exists);
    [PreserveSig] int GetLocaleNameLength(uint index, out uint length);
    [PreserveSig] int GetLocaleName(uint index, char* locale, uint size);
    [PreserveSig] int GetStringLength(uint index, out uint length);
    [PreserveSig] int GetString(uint index, char* text, uint size);
}
