﻿using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace Merger_exporter;

internal class GtfsFile(string name, Stream stream) : IAsyncDisposable
{
    public string Name => name;

    private readonly List<FileInfo> _tempFiles = [];
    private readonly List<string> _tempFolders = [];

    private async ValueTask<FileInfo> DownloadAsync(CancellationToken cancellationToken)
    {
        var tempFileName = Path.GetTempFileName();
        var tempFile = new FileInfo(tempFileName);
        _tempFiles.Add(tempFile);
        using var fileStream = tempFile.Open(FileMode.Create);
        Console.WriteLine($"Using temp file {tempFileName}");
        await stream.CopyToAsync(fileStream, cancellationToken);
        Console.WriteLine($"Downloaded {Name} to {tempFileName}");
        return tempFile;
    }

    private async ValueTask<string> UnzipFiles(CancellationToken cancellationToken)
    {
        var file = await DownloadAsync(cancellationToken);
        var tempFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempFolders.Add(tempFolder);
        await Task.Run(
            () => ZipFile.ExtractToDirectory(file.FullName, tempFolder),
            cancellationToken
        );
        Console.WriteLine($"Unzipped {file.FullName} to {tempFolder}");
        return tempFolder;
    }

    public async IAsyncEnumerable<string> GetFoldersFilesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var tempFolder = await UnzipFiles(cancellationToken);
        foreach (var entry in Directory.EnumerateFileSystemEntries(tempFolder))
        {
            if (Directory.Exists(entry))
            {
                Console.WriteLine($"Folder: {entry}");

                //Extract subGtfsFiles
                foreach (var subGtfsFile in Directory.EnumerateFiles(entry))
                {
                    if (Path.GetExtension(subGtfsFile) != ".zip")
                        continue;

                    using var fileStream = File.Open(subGtfsFile, FileMode.Open);
                    var gtfsFile = new GtfsFile(Path.GetFileName(subGtfsFile), fileStream);
                    await foreach (var item in gtfsFile.GetFoldersFilesAsync(cancellationToken))
                    {
                        yield return item;
                    }
                }
            }
            else if (File.Exists(entry))
            {
                //Leave as it is
                Console.WriteLine($"File: {entry}");
                yield return entry;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("Disposing...");
        foreach (var file in _tempFiles)
        {
            file.Delete();
        }
        foreach (var folder in _tempFolders)
        {
            Directory.Delete(folder, true);
        }
        await stream.DisposeAsync();
    }
}
