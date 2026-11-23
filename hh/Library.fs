namespace hh

open System
open System.IO
open System.Net.Http
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading

module private DownloadHelpers =
    let sanitizeFileName (name: string) =
        let invalid = Path.GetInvalidFileNameChars() |> Set.ofArray
        let cleaned = name |> Seq.map (fun character -> if invalid.Contains character then '_' else character) |> String.Concat
        if String.IsNullOrWhiteSpace cleaned then "video.bin" else cleaned

    let fileNameFromUrl (url: Uri) =
        let segment = url.Segments |> Array.tryLast |> Option.defaultValue "video.bin"
        let decoded = Uri.UnescapeDataString(segment.TrimEnd('/'))
        if String.IsNullOrWhiteSpace decoded then "video.bin" else sanitizeFileName decoded

    let formatBytes bytes =
        if bytes < 1024L then sprintf "%d B" bytes
        elif bytes < 1024L * 1024L then sprintf "%.1f KB" (float bytes / 1024.0)
        elif bytes < 1024L * 1024L * 1024L then sprintf "%.1f MB" (float bytes / (1024.0 * 1024.0))
        else sprintf "%.2f GB" (float bytes / (1024.0 * 1024.0 * 1024.0))

type MainWindow() as this =
    inherit Window()
    let urlBox = TextBox(Watermark = "https://example.com/video.mp4", HorizontalAlignment = HorizontalAlignment.Stretch)
    let folderBox = TextBox(IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch)
    let statusText = TextBlock(TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray)
    let progress = ProgressBar(Minimum = 0.0, Maximum = 100.0, Height = 14.0)
    let downloadButton = Button(Content = "Download", MinWidth = 110.0)
    let cancelButton = Button(Content = "Cancel", MinWidth = 110.0, IsEnabled = false)
    let mutable selectedFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    let mutable cancellation: CancellationTokenSource option = None
    let httpClient = new HttpClient()

    let setStatus text = Dispatcher.UIThread.Post(fun () -> statusText.Text <- text)

    let chooseFolder () =
        task {
            let options = FolderPickerOpenOptions(Title = "Choose download folder", AllowMultiple = false)
            let! folders = this.StorageProvider.OpenFolderPickerAsync(options)
            match folders |> Seq.tryHead with
            | Some folder ->
                let path = folder.Path.LocalPath
                if String.IsNullOrWhiteSpace path then
                    setStatus "The selected folder is not available as a local path."
                else
                    selectedFolder <- path
                    folderBox.Text <- path
            | None -> ()
        }

    let download () =
        async {
            let mutable parsedUrl = Unchecked.defaultof<Uri>
            if not (Uri.TryCreate(urlBox.Text, UriKind.Absolute, &parsedUrl)) || (parsedUrl.Scheme <> Uri.UriSchemeHttp && parsedUrl.Scheme <> Uri.UriSchemeHttps) then
                setStatus "Enter a valid HTTP or HTTPS direct media URL."
            else
                let targetPath = Path.Combine(selectedFolder, DownloadHelpers.fileNameFromUrl parsedUrl)
                try
                    Directory.CreateDirectory(selectedFolder) |> ignore
                    use request = new HttpRequestMessage(HttpMethod.Get, parsedUrl)
                    use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
                    response.EnsureSuccessStatusCode()
                    use! input = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                    use output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true)
                    let buffer = Array.zeroCreate<byte> 81920
                    let total = response.Content.Headers.ContentLength
                    let mutable copied = 0L
                    let mutable reading = true
                    while reading do
                        let! count = input.ReadAsync(buffer, 0, buffer.Length, cancellationToken = cancellation.Value.Token) |> Async.AwaitTask
                        if count = 0 then reading <- false
                        else
                            do! output.WriteAsync(buffer, 0, count, cancellationToken = cancellation.Value.Token) |> Async.AwaitTask
                            copied <- copied + int64 count
                            if total.HasValue && total.Value > 0L then
                                let size = total.Value
                                Dispatcher.UIThread.Post(fun () -> progress.Value <- float copied * 100.0 / float size)
                                setStatus (sprintf "Downloading: %s of %s" (DownloadHelpers.formatBytes copied) (DownloadHelpers.formatBytes size))
                            else
                                setStatus (sprintf "Downloading: %s" (DownloadHelpers.formatBytes copied))
                    setStatus (sprintf "Saved to %s" targetPath)
                with
                | :? OperationCanceledException -> setStatus "Download cancelled."
                | ex ->
                    if File.Exists targetPath then File.Delete targetPath
                    setStatus (sprintf "Download failed: %s" ex.Message)
                Dispatcher.UIThread.Post(fun () -> downloadButton.IsEnabled <- true; cancelButton.IsEnabled <- false)
        }

    do
        this.Title <- "Direct Video Downloader"
        this.Width <- 720.0
        this.Height <- 330.0
        this.MinWidth <- 520.0
        this.MinHeight <- 280.0
        this.WindowStartupLocation <- WindowStartupLocation.CenterScreen
        folderBox.Text <- selectedFolder
        statusText.Text <- "Ready. Use direct media URLs only."
        let folderButton = Button(Content = "Choose folder...", MinWidth = 130.0)
        let folderRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0)
        folderRow.Children.Add(folderBox) |> ignore
        folderRow.Children.Add(folderButton) |> ignore
        let actionRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0, HorizontalAlignment = HorizontalAlignment.Right)
        actionRow.Children.Add(cancelButton) |> ignore
        actionRow.Children.Add(downloadButton) |> ignore
        let panel = StackPanel(Spacing = 12.0, Margin = Thickness(24.0))
        panel.Children.Add(TextBlock(Text = "Direct Video Downloader", FontSize = 24.0, FontWeight = FontWeight.Bold)) |> ignore
        panel.Children.Add(TextBlock(Text = "Download media from a direct HTTP(S) URL to your computer.")) |> ignore
        panel.Children.Add(TextBlock(Text = "Media URL")) |> ignore
        panel.Children.Add(urlBox) |> ignore
        panel.Children.Add(TextBlock(Text = "Save folder")) |> ignore
        panel.Children.Add(folderRow) |> ignore
        panel.Children.Add(progress) |> ignore
        panel.Children.Add(statusText) |> ignore
        panel.Children.Add(actionRow) |> ignore
        panel.Children.Add(TextBlock(Text = "Only download content you are authorized to save. DRM, login-protected pages, and streaming manifests are not supported.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12.0)) |> ignore
        this.Content <- panel
        folderButton.Click.Add(fun _ -> chooseFolder() |> ignore)
        cancelButton.Click.Add(fun _ -> cancellation |> Option.iter (fun source -> source.Cancel()))
        downloadButton.Click.Add(fun _ ->
            downloadButton.IsEnabled <- false
            cancelButton.IsEnabled <- true
            progress.Value <- 0.0
            let source = new CancellationTokenSource()
            cancellation <- Some source
            Async.Start(download(), source.Token))

    override this.OnClosed args =
        cancellation |> Option.iter (fun source -> source.Cancel(); source.Dispose())
        httpClient.Dispose()
        base.OnClosed(args)

type DownloaderApp() =
    inherit Application()
    override _.Initialize() = ()
    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- MainWindow()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()

module Program =
    [<EntryPoint>]
    let main args =
        AppBuilder.Configure<DownloaderApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args)
