using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Core.Sync;

public static class SyncPreview
{
    /// <summary>The simulation of a copy-engine job, in the same shape as a robocopy /L preview.</summary>
    public static PreviewResult From(SyncPlan plan, ISourceStore source, ISourceStore target, CopyJob job)
    {
        var r = new PreviewResult
        {
            Mode = job.Mode,
            Source = job.Source.Path,
            Destination = job.Destination.Path,
            DestinationExists = plan.DestinationExists,
            KeepVersions = job.Options.KeepVersions,
            FilesToCopy = plan.Copies.Count,
            BytesToCopy = plan.Copies.Sum(c => c.Source.Size),
            FilesUnchanged = plan.Unchanged,
            BytesUnchanged = plan.UnchangedBytes,
        };
        foreach (var c in plan.Copies.Take(PreviewResult.MaxCopyItems))
            r.CopyItems.Add(new PreviewItem { Path = source.Display(c.Source.Relative), Size = c.Source.Size });
        r.CopyItemsTruncated = plan.Copies.Count > PreviewResult.MaxCopyItems;

        foreach (var x in plan.Extras)
        {
            if (x.Entry.IsDirectory) r.ExtraDirs++;
            r.ExtraFiles += x.Files;
            r.ExtraBytes += x.Bytes;
            if (r.ExtraItems.Count < PreviewResult.MaxDeleteItems)
                r.ExtraItems.Add(new PreviewItem
                {
                    Path = target.Display(x.Entry.Relative),
                    Size = x.Bytes,
                    IsDirectory = x.Entry.IsDirectory,
                    Files = x.Entry.IsDirectory ? x.Files : null,
                });
        }
        r.ExtraItemsTruncated = plan.Extras.Count > PreviewResult.MaxDeleteItems;
        return r;
    }
}
