using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Deliveries;

internal sealed class DeliveryExportFiles(DataDirectoryLock dataLock, DeliveryCache cache, Func<StorageSpace?>? readSpace = null)
    : ExportFile(dataLock, cache.Options, DirectoryName, FileName, readSpace)
{
    internal const string DirectoryName = "delivery-export";
    internal const string FileName = "snapshot.jsonl.tmp";
}
