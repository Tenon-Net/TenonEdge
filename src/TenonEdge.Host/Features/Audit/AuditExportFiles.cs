using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Audit;

internal sealed class AuditExportFiles(DataDirectoryLock dataLock, DeliveryCache cache, Func<StorageSpace?>? readSpace = null)
    : ExportFile(dataLock, cache.Options, DirectoryName, FileName, readSpace)
{
    internal const string DirectoryName = "audit-export";
    internal const string FileName = "snapshot.csv.tmp";
}
