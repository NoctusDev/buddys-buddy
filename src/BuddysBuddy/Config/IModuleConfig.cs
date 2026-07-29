namespace BuddysBuddy.Config;

/// <summary>
/// Marker for a module's own settings blob.
/// </summary>
/// <remarks>
/// Implementations are stored inside the host <see cref="Configuration"/> keyed by the module's
/// <c>InternalName</c> and serialised polymorphically, so they must be plain, public,
/// parameterless-constructible classes with public properties.
/// </remarks>
public interface IModuleConfig
{
}
