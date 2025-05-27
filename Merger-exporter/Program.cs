using System.IO.Compression;
using System.Runtime.CompilerServices;
using ConsoleAppFramework;
using Merger_exporter;
using nietras.SeparatedValues;
using ZLinq;

[assembly: ZLinq.ZLinqDropInAttribute("", ZLinq.DropInGenerateTypes.Everything)]

List<(string name, string url)> gtfsFilesUrls =
[
    (
        "google_transit_M9",
        "https://www.arcgis.com/sharing/rest/content/items/357e63c2904f43aeb5d8a267a64346d8/data"
    ),
    (
        "google_transit_M89",
        "https://www.arcgis.com/sharing/rest/content/items/885399f83408473c8d815e40c5e702b7/data"
    ),
    (
        "google_transit_M4",
        "https://www.arcgis.com/sharing/rest/content/items/5c7f2951962540d69ffe8f640d94c246/data"
    ),
    (
        "google_transit_M6",
        "https://www.arcgis.com/sharing/rest/content/items/868df0e58fca47e79b942902dffd7da0/data"
    ),
    (
        "google_transit_M10",
        "https://www.arcgis.com/sharing/rest/content/items/aaed26cc0ff64b0c947ac0bc3e033196/data"
    ),
    (
        "google_transit_M5",
        "https://www.arcgis.com/sharing/rest/content/items/1a25440bf66f499bae2657ec7fb40144/data"
    ),
];

List<(string name, string url)> otherFiles =
[
    (
        "Metro_stations",
        "https://hub.arcgis.com/api/download/v1/items/0a6c45e7bdd94679b67a2ae662c8838b/csv?redirect=true&layers=0"
    ),
    (
        "Train_stations",
        "https://hub.arcgis.com/api/download/v1/items/9e353bbf4c5d4bea87f01d6d579d06ab/csv?redirect=true&layers=0"
    ),
    (
        "Train_itineraries",
        "https://hub.arcgis.com/api/download/v1/items/9e353bbf4c5d4bea87f01d6d579d06ab/csv?redirect=true&layers=5"
    )
];

(string name, string url, int[] layers) otherTramFiles = (
    "Tram_stations",
    "https://hub.arcgis.com/api/download/v1/items/b56d0a20a21d4b7eb1721d9f328ea3ae/csv?redirect=true&layers=replace",
    [2, 6, 11, 15, 20, 24, 29, 33]
);

List<FileInfo> tempFiles = [];

await ConsoleApp.RunAsync(
    args,
    async (CancellationToken token, string destinationFolder) =>
    {
        using var httpClient = new HttpClient();

        async IAsyncEnumerable<GtfsFile> GetGtfsFilesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            foreach (var (name, url) in gtfsFilesUrls)
            {
                var gtfsFile = new GtfsFile(
                    name,
                    await httpClient.GetStreamAsync(url, cancellationToken)
                );
                yield return gtfsFile;
            }
        }

        var gtfsFiles = await GetGtfsFilesAsync(token).ToListAsync(cancellationToken: token);

        try
        {
            List<Task> tasks = [];

            foreach (var item in gtfsFiles)
            {
                var gtfsFilesTask = Task.Run(
                    async () =>
                    {
                        var files = await item.GetFoldersFilesAsync(token)
                            .ToListAsync(cancellationToken: token);
                        var grouped = files.AsValueEnumerable().GroupBy(i => Path.GetFileName(i)).ToList();

                        var folder = Directory.CreateDirectory(
                            Path.Combine(destinationFolder, item.Name)
                        );

                        using var merger = await Merger.CreateMergerAsync(
                            grouped,
                            folder.FullName,
                            token
                        );
                        await merger.MergeAsync(token);

                        Console.WriteLine($"Compressing {folder.FullName}");

                        ZipFile.CreateFromDirectory(
                            folder.FullName,
                            folder.FullName + ".zip",
                            CompressionLevel.Optimal,
                            includeBaseDirectory: false
                        );
                    },
                    token
                );
                tasks.Add(gtfsFilesTask);
            }

            foreach (var (name, url) in otherFiles)
            {
                var otherFilesTask = Task.Run(
                    async () =>
                    {
                        Console.WriteLine($"Downloading {name}");
                        var path = Path.Combine(destinationFolder, name + ".csv");
                        using var fileStream = await httpClient.GetStreamAsync(url, token);
                        Console.WriteLine($"Downloaded {name}");

                        using var reader = await Sep.Reader(o =>
                                o with
                                {
                                    HasHeader = true,
                                    Unescape = true,
                                    DisableColCountCheck = true,
                                }
                            )
                            .FromAsync(fileStream, token);

                        await using var writer = Sep.Writer(o =>
                                o with
                                {
                                    Sep = new Sep(','),
                                    WriteHeader = true,
                                    ColNotSetOption = SepColNotSetOption.Empty,
                                    Escape = true
                                }
                            )
                            .ToFile(path);

                        var header = reader.Header;

                        await foreach (var row in reader)
                        {
                            using var newRow = writer.NewRow(row);
                            if (header.ColNames.Contains("OBSERVACIONES"))
                            {
                                var data = row["OBSERVACIONES"].ToString();
                                newRow["OBSERVACIONES"].Set(data.Replace("\r\n", " "));
                            }
                        }
                    },
                    token
                );
                tasks.Add(otherFilesTask);
            }

            List<Task> downloadTasks = [];

            async Task DownloadOtherTramFile(int layer, CancellationToken token)
            {
                Console.WriteLine($"Downloading layer {layer} for of {otherTramFiles.name}");
                var tempFilePath = Path.GetTempFileName();
                var tempFile = new FileInfo(tempFilePath);
                tempFiles.Add(tempFile);
                using var tempFileStream = tempFile.Open(FileMode.OpenOrCreate);
                using var fileStream = await httpClient.GetStreamAsync(
                    otherTramFiles.url.Replace("replace", layer.ToString()),
                    token
                );
                await fileStream.CopyToAsync(tempFileStream, token);
            }

            foreach (var layer in otherTramFiles.layers)
            {
                downloadTasks.Add(DownloadOtherTramFile(layer, token));
            }

            await Task.WhenAll(downloadTasks);

            var otherTramFilesTask = Task.Run(
                async () =>
                {
                    var path = Path.Combine(destinationFolder, otherTramFiles.name + ".csv");
                    await using var writer = Sep.Writer(o =>
                            o with
                            {
                                Sep = new Sep(','),
                                Escape = true,
                                WriteHeader = true
                            }
                        )
                        .ToFile(path);
                    var set = new HashSet<string>();
                    foreach (var tempFile in tempFiles)
                    {
                        using var reader = await Sep.Reader(o =>
                                o with
                                {
                                    HasHeader = true,
                                    DisableQuotesParsing = true,
                                    Unescape = true
                                }
                            )
                            .FromFileAsync(tempFile.FullName, token);
                        await foreach (var row in reader)
                        {
                            var id = row["IDESTACION"].ToString();
                            if (!set.Add(id))
                            {
                                continue;
                            }
                            using var _ = writer.NewRow(row);
                        }
                        await writer.FlushAsync(token);
                    }
                },
                token
            );

            tasks.Add(otherTramFilesTask);

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var item in gtfsFiles)
            {
                await item.DisposeAsync();
            }

            foreach (var item in tempFiles)
            {
                item.Delete();
            }
        }
    }
);
