using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class RemoteConfigLoader : MonoBehaviour
{
    private const string DefaultConfigUrl = "https://s3.amazonaws.com/mybucket/config.json";

    [Header("Remote Config")]
    [SerializeField] private string configUrl = DefaultConfigUrl;
    [SerializeField] private int requestTimeoutSeconds = 15;
    [SerializeField] private bool startOnAwake = true;

    [Header("Scene")]
    [SerializeField] private bool loadMainSceneAfterCatalog = true;
    [SerializeField] private string mainSceneName = "Game";
    [SerializeField] private LoadSceneMode mainSceneLoadMode = LoadSceneMode.Single;

    [Header("Loading Events")]
    [SerializeField] private RemoteConfigStatusEvent onStatusChanged = new RemoteConfigStatusEvent();
    [SerializeField] private RemoteConfigProgressEvent onProgressChanged = new RemoteConfigProgressEvent();
    [SerializeField] private RemoteConfigLoadedEvent onConfigLoaded = new RemoteConfigLoadedEvent();
    [SerializeField] private RemoteCatalogLoadedEvent onCatalogLoaded = new RemoteCatalogLoadedEvent();
    [SerializeField] private RemoteConfigFailedEvent onFailed = new RemoteConfigFailedEvent();
    [SerializeField] private UnityEvent onCompleted = new UnityEvent();

    private CancellationTokenSource cancellationTokenSource;
    private bool isLoading;

    public event Action<string> StatusChanged;
    public event Action<float> ProgressChanged;
    public event Action<RemoteCatalogConfig> ConfigLoaded;
    public event Action<string> CatalogLoaded;
    public event Action<RemoteConfigLoadError> Failed;
    public event Action Completed;

    public bool IsLoading => isLoading;
    public string ConfigUrl => configUrl;

    private void Awake()
    {
        if (startOnAwake)
        {
            LoadAsync().Forget();
        }
    }

    private void OnDestroy()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
    }

    public async UniTask LoadAsync()
    {
        if (isLoading)
        {
            return;
        }

        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = new CancellationTokenSource();

        await LoadAsync(cancellationTokenSource.Token);
    }

    public async UniTask LoadAsync(CancellationToken cancellationToken)
    {
        if (isLoading)
        {
            return;
        }

        isLoading = true;
        ReportProgress(0f);

        try
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                throw new RemoteConfigException(
                    RemoteConfigErrorType.NoInternet,
                    "Internet connection is not available.");
            }

            if (string.IsNullOrWhiteSpace(configUrl))
            {
                throw new RemoteConfigException(
                    RemoteConfigErrorType.InvalidConfigUrl,
                    "Remote config URL is empty.");
            }

            ReportStatus("Config indiriliyor...");
            var configJson = await DownloadTextAsync(configUrl, cancellationToken);
            ReportProgress(0.35f);

            ReportStatus("Config okunuyor...");
            var config = ParseConfig(configJson);
            var platformConfig = GetCurrentPlatformConfig(config);
            ReportConfigLoaded(config);
            ReportProgress(0.45f);

            ReportStatus("Addressables catalog yukleniyor...");
            await LoadAddressablesCatalogAsync(platformConfig.catalog_url, cancellationToken);
            ReportCatalogLoaded(platformConfig.catalog_url);
            ReportProgress(0.85f);

            if (loadMainSceneAfterCatalog)
            {
                await LoadMainSceneAsync(cancellationToken);
            }

            ReportProgress(1f);
            ReportStatus("Hazir.");
            ReportCompleted();
        }
        catch (OperationCanceledException)
        {
            ReportFailed(new RemoteConfigLoadError(
                RemoteConfigErrorType.Cancelled,
                "Remote config loading was cancelled.",
                null));
        }
        catch (RemoteConfigException exception)
        {
            ReportFailed(new RemoteConfigLoadError(exception.ErrorType, exception.Message, exception));
        }
        catch (Exception exception)
        {
            ReportFailed(new RemoteConfigLoadError(
                RemoteConfigErrorType.Unknown,
                exception.Message,
                exception));
        }
        finally
        {
            isLoading = false;
        }
    }

    private async UniTask<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = UnityWebRequest.Get(url);
        request.timeout = Mathf.Max(1, requestTimeoutSeconds);

        var operation = request.SendWebRequest();
        while (!operation.isDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(Mathf.Lerp(0f, 0.35f, operation.progress));
            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
        }

        if (request.result == UnityWebRequest.Result.ConnectionError)
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.ConfigRequestFailed,
                $"Remote config connection failed: {request.error}");
        }

        if (request.result == UnityWebRequest.Result.ProtocolError)
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.ConfigRequestFailed,
                $"Remote config request failed. HTTP {request.responseCode}: {request.error}");
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.ConfigRequestFailed,
                $"Remote config request failed: {request.error}");
        }

        var text = request.downloadHandler.text;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.EmptyConfig,
                "Remote config response is empty.");
        }

        return text;
    }

    private static RemoteCatalogConfig ParseConfig(string json)
    {
        try
        {
            var config = JsonUtility.FromJson<RemoteCatalogConfig>(json);
            if (config == null)
            {
                throw new RemoteConfigException(
                    RemoteConfigErrorType.InvalidConfigJson,
                    "Remote config JSON could not be parsed.");
            }

            return config;
        }
        catch (RemoteConfigException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.InvalidConfigJson,
                $"Remote config JSON is invalid: {exception.Message}");
        }
    }

    private static RemoteCatalogPlatformConfig GetCurrentPlatformConfig(RemoteCatalogConfig config)
    {
        var platformConfig = GetPlatformConfig(config);
        if (platformConfig == null)
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.MissingPlatformConfig,
                $"Remote config does not include a catalog URL for {Application.platform}.");
        }

        if (string.IsNullOrWhiteSpace(platformConfig.catalog_url))
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.MissingCatalogUrl,
                $"Catalog URL is empty for {Application.platform}.");
        }

        return platformConfig;
    }

    private static RemoteCatalogPlatformConfig GetPlatformConfig(RemoteCatalogConfig config)
    {
#if UNITY_ANDROID
        return config.android;
#elif UNITY_IOS
        return config.ios;
#else
        if (Application.platform == RuntimePlatform.Android)
        {
            return config.android;
        }

        if (Application.platform == RuntimePlatform.IPhonePlayer)
        {
            return config.ios;
        }

        return config.android ?? config.ios;
#endif
    }

    private async UniTask LoadAddressablesCatalogAsync(string catalogUrl, CancellationToken cancellationToken)
    {
        AsyncOperationHandle<UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator> handle = Addressables.LoadContentCatalogAsync(
            catalogUrl,
            autoReleaseHandle: false);

        try
        {
            while (!handle.IsDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(Mathf.Lerp(0.45f, 0.85f, handle.PercentComplete));
                await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
            }

            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                return;
            }

            throw new RemoteConfigException(
                RemoteConfigErrorType.CatalogLoadFailed,
                handle.OperationException?.Message ?? "Addressables catalog loading failed.");
        }
        finally
        {
            if (handle.IsValid())
            {
                Addressables.Release(handle);
            }
        }
    }

    private async UniTask LoadMainSceneAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mainSceneName))
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.MainSceneLoadFailed,
                "Main scene name is empty.");
        }

        ReportStatus("Ana sahne aciliyor...");
        var operation = SceneManager.LoadSceneAsync(mainSceneName, mainSceneLoadMode);
        if (operation == null)
        {
            throw new RemoteConfigException(
                RemoteConfigErrorType.MainSceneLoadFailed,
                $"Main scene could not be loaded: {mainSceneName}");
        }

        while (!operation.isDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(Mathf.Lerp(0.85f, 1f, operation.progress));
            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
        }
    }

    private void ReportStatus(string message)
    {
        onStatusChanged.Invoke(message);
        StatusChanged?.Invoke(message);
    }

    private void ReportProgress(float progress)
    {
        progress = Mathf.Clamp01(progress);
        onProgressChanged.Invoke(progress);
        ProgressChanged?.Invoke(progress);
    }

    private void ReportConfigLoaded(RemoteCatalogConfig config)
    {
        onConfigLoaded.Invoke(config);
        ConfigLoaded?.Invoke(config);
    }

    private void ReportCatalogLoaded(string catalogUrl)
    {
        onCatalogLoaded.Invoke(catalogUrl);
        CatalogLoaded?.Invoke(catalogUrl);
    }

    private void ReportFailed(RemoteConfigLoadError error)
    {
        Debug.LogError($"Remote config load failed. Type: {error.ErrorType}, Message: {error.Message}");
        onFailed.Invoke(error);
        Failed?.Invoke(error);
    }

    private void ReportCompleted()
    {
        onCompleted.Invoke();
        Completed?.Invoke();
    }
}

[Serializable]
public class RemoteCatalogConfig
{
    public RemoteCatalogPlatformConfig android;
    public RemoteCatalogPlatformConfig ios;
}

[Serializable]
public class RemoteCatalogPlatformConfig
{
    public string catalog_url;
}

public enum RemoteConfigErrorType
{
    None,
    NoInternet,
    InvalidConfigUrl,
    ConfigRequestFailed,
    EmptyConfig,
    InvalidConfigJson,
    MissingPlatformConfig,
    MissingCatalogUrl,
    CatalogLoadFailed,
    MainSceneLoadFailed,
    Cancelled,
    Unknown
}

[Serializable]
public class RemoteConfigLoadError
{
    [SerializeField] private RemoteConfigErrorType errorType;
    [SerializeField] private string message;
    [SerializeField] private string exceptionType;

    public RemoteConfigLoadError(RemoteConfigErrorType errorType, string message, Exception exception)
    {
        this.errorType = errorType;
        this.message = message;
        exceptionType = exception != null ? exception.GetType().Name : string.Empty;
    }

    public RemoteConfigErrorType ErrorType => errorType;
    public string Message => message;
    public string ExceptionType => exceptionType;
}

public class RemoteConfigException : Exception
{
    public RemoteConfigException(RemoteConfigErrorType errorType, string message)
        : base(message)
    {
        ErrorType = errorType;
    }

    public RemoteConfigErrorType ErrorType { get; }
}

[Serializable]
public class RemoteConfigStatusEvent : UnityEvent<string>
{
}

[Serializable]
public class RemoteConfigProgressEvent : UnityEvent<float>
{
}

[Serializable]
public class RemoteConfigLoadedEvent : UnityEvent<RemoteCatalogConfig>
{
}

[Serializable]
public class RemoteCatalogLoadedEvent : UnityEvent<string>
{
}

[Serializable]
public class RemoteConfigFailedEvent : UnityEvent<RemoteConfigLoadError>
{
}
