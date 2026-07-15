namespace TopologicalMaterialField;

internal static class ShaderResourceUri
{
    public static Uri Get(string shaderName) => new($"pack://application:,,,/TopologicalMaterialField;component/Shaders/{shaderName}.cso", UriKind.Absolute);
}
