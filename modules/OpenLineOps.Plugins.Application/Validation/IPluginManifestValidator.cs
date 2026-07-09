using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Application.Validation;

public interface IPluginManifestValidator
{
    PluginValidationReport Validate(PluginManifest manifest);
}
