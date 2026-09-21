namespace Betsi.Tests.UI;

using Betsi.UI.Resources;
using System.Collections;
using System.Globalization;
using System.Resources;

public sealed class TranslationTests
{
    [Fact]
    public void Welsh_has_every_English_resource_without_relying_on_fallback()
    {
        var resources = new ResourceManager(typeof(Strings));
        var english = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var welsh = resources.GetResourceSet(CultureInfo.GetCultureInfo("cy-GB"), true, false)!;
        welsh.ShouldNotBeNull();
        var englishKeys = english.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).Order().ToArray();
        var welshKeys = welsh.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).Order().ToArray();
        welshKeys.ShouldBe(englishKeys);
        foreach (var key in englishKeys)
            string.IsNullOrWhiteSpace(welsh.GetString(key)).ShouldBeFalse(key);
    }
}
