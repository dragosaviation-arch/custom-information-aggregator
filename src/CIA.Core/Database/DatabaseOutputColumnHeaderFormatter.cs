using CIA.Contracts.Database;
using CIA.Contracts.Discovery;

namespace CIA.Core.Database;

public static class DatabaseOutputColumnHeaderFormatter
{
    public static string Resolve(
        DatabaseDatasetBuildSpecification dataset,
        DatabaseColumnIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var field = dataset.Fields.Single(mapping =>
            string.Equals(mapping.FieldKey, identity.FieldKey, StringComparison.Ordinal));
        if (dataset.ProjectionMode == DiscoveryOutputProjectionMode.OneRowPerSourceFile
            && identity.RepeatCoordinates.Coordinates.Count > 0)
        {
            var identityPath = field.DetailedIdentities[0].StructuralPath;
            var coordinateSegments = dataset.SourceProjectionRepeatedPaths
                .Where(path => string.Equals(path, identityPath, StringComparison.Ordinal)
                    || identityPath.StartsWith(path + "/", StringComparison.Ordinal))
                .OrderBy(path => path.Count(character => character == '/'))
                .Select(GetFinalPathSegment)
                .Zip(identity.RepeatCoordinates.Coordinates)
                .Select(pair => $"{pair.First}[{pair.Second}]");
            return $"{string.Join('.', coordinateSegments)}.{field.EffectiveName}";
        }

        return identity.RepeatCoordinates.Coordinates.Count == 0
            ? field.EffectiveName
            : $"{field.EffectiveName} {identity.RepeatCoordinates}";
    }

    private static string GetFinalPathSegment(string path)
    {
        var segment = path[(path.LastIndexOf('/') + 1)..];
        var namespaceEnd = segment.LastIndexOf('}');
        return namespaceEnd >= 0 && namespaceEnd < segment.Length - 1
            ? segment[(namespaceEnd + 1)..]
            : segment;
    }
}
