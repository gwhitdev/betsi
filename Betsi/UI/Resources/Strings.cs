namespace Betsi.UI.Resources;

/// <summary>
/// Marker for the interface's string resources. <c>IStringLocalizer&lt;Strings&gt;</c> resolves
/// against <c>Strings.resx</c> and its per-language siblings.
/// </summary>
/// <remarks>
/// Every visible string goes through here, including the English. A UI with English hard-coded
/// into markup and Welsh bolted on afterwards ends up with two markups that drift; one set of
/// keys and two resource files cannot.
///
/// The Welsh translations were produced by the developer, not by a Welsh speaker, and are
/// marked as needing review before any pilot — see docs/runbooks/welsh-language.md.
/// </remarks>
public sealed class Strings;
