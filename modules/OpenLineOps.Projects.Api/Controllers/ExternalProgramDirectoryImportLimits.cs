using OpenLineOps.Projects.Application.ExternalPrograms;

namespace OpenLineOps.Projects.Api.Controllers;

public static class ExternalProgramDirectoryImportLimits
{
    public const int MaximumFileCount = ExternalProgramResourceContract.MaximumFrozenFileCount;
    public const long MaximumFileBytes = ExternalProgramResourceContract.MaximumFrozenFileBytes;
    public const long MaximumContentBytes = ExternalProgramResourceContract.MaximumFrozenTotalBytes;
    public const long MaximumRequestMetadataBytes = 8L * 1024 * 1024;
    public const long MaximumRequestBytes = MaximumContentBytes + MaximumRequestMetadataBytes;
    public const int MaximumFormValueBytes = 1024 * 1024;

    public static bool CanAccumulateContentBytes(long accumulatedBytes, long nextFileBytes) =>
        accumulatedBytes >= 0
        && nextFileBytes >= 0
        && accumulatedBytes <= MaximumContentBytes
        && nextFileBytes <= MaximumContentBytes - accumulatedBytes;
}
