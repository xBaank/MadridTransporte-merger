using nietras.SeparatedValues;

namespace Merger_exporter;

class Merger(
    string destinationFolder,
    Dictionary<string, List<SepReader>> readersByFileName,
    Dictionary<string, SepWriter> writersByFileName
) : IDisposable
{
    public static async Task<Merger> CreateMergerAsync(
        List<IGrouping<string, string>> gtfsFilesByName,
        string destinationFolder,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, List<SepReader>> readersByFileName = [];
        Dictionary<string, SepWriter> writersByFileName = [];

        foreach (var gtfsFiles in gtfsFilesByName)
        {
            var fileName = Path.GetFileName(gtfsFiles.Key);
            var path = Path.Combine(destinationFolder, fileName);

            if (!writersByFileName.ContainsKey(gtfsFiles.Key))
            {
                Console.WriteLine($"Creating writer for {path}");
                writersByFileName[gtfsFiles.Key] = Sep.Writer(o =>
                        o with
                        {
                            Sep = new Sep(','),
                            Escape = true,
                            WriteHeader = true
                        }
                    )
                    .ToFile(path);
            }

            foreach (var gtfsFile in gtfsFiles)
            {
                var reader = await Sep.Reader(o =>
                        o with
                        {
                            HasHeader = true,
                            DisableQuotesParsing = true,
                            Unescape = true
                        }
                    )
                    .FromFileAsync(gtfsFile, cancellationToken);
                if (readersByFileName.TryGetValue(gtfsFiles.Key, out var value))
                {
                    value.Add(reader);
                }
                else
                {
                    readersByFileName[gtfsFiles.Key] = [];
                    readersByFileName[gtfsFiles.Key].Add(reader);
                }
            }
        }

        return new Merger(destinationFolder, readersByFileName, writersByFileName);
    }

    public async Task MergeAsync(CancellationToken cancellationToken)
    {
        List<Task> tasks = [];
        foreach (var (file, writer) in writersByFileName)
        {
            var task = Task.Run(
                async () => await MergeToFile(file, writer, cancellationToken),
                cancellationToken
            );
            tasks.Add(task);
        }
        await Task.WhenAll(tasks);
    }

    private async Task MergeToFile(
        string file,
        SepWriter writer,
        CancellationToken cancellationToken
    )
    {
        await using var _writer = writer;
        var readers = readersByFileName[file];
        foreach (var reader in readers)
        {
            using var _reader = reader;

            await foreach (var item in reader)
            {
                await using var row = writer.NewRow(item, cancellationToken: cancellationToken);
            }
        }
        await writer.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        Directory.Delete(destinationFolder, true);
    }
}
