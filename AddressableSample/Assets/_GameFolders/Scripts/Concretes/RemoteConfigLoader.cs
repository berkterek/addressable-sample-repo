using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

public sealed class RemoteConfigLoader
{
    private const string ProductionConfigUrl = "https://terek-addressables-sample-prod.s3.us-east-1.amazonaws.com/production/config.json";
    private const string StagingConfigUrl = "https://terek-addressables-sample-prod.s3.us-east-1.amazonaws.com/staging/config.json";

    private readonly int requestTimeoutSeconds;
    private bool isLoading;

    public RemoteConfigLoader(int requestTimeoutSeconds = 15)
    {
        this.requestTimeoutSeconds = Mathf.Max(1, requestTimeoutSeconds);
    }

    public event Action<string> StatusChanged;
    public event Action<float> ProgressChanged;
    public event Action<RemoteCatalogConfig> ConfigLoaded;
    public event Action<string> CatalogLoaded;
    public event Action<RemoteConfigLoadError> Failed;
    public event Action Completed;

    public bool IsLoading => isLoading;

    public string ConfigUrl
    {
        get
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return StagingConfigUrl;
#else
            return ProductionConfigUrl;
#endif
        }
    }

    public async UniTask LoadAsync(CancellationToken cancellationToken = default)
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

            var configUrl = ConfigUrl;
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
            throw;
        }
        catch (RemoteConfigException exception)
        {
            ReportFailed(new RemoteConfigLoadError(exception.ErrorType, exception.Message, exception));
            throw;
        }
        catch (Exception exception)
        {
            ReportFailed(new RemoteConfigLoadError(
                RemoteConfigErrorType.Unknown,
                exception.Message,
                exception));
            throw;
        }
        finally
        {
            isLoading = false;
        }
    }

    private async UniTask<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = UnityWebRequest.Get(url);
        request.timeout = requestTimeoutSeconds;

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

    private void ReportStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    private void ReportProgress(float progress)
    {
        ProgressChanged?.Invoke(Mathf.Clamp01(progress));
    }

    private void ReportConfigLoaded(RemoteCatalogConfig config)
    {
        ConfigLoaded?.Invoke(config);
    }

    private void ReportCatalogLoaded(string catalogUrl)
    {
        CatalogLoaded?.Invoke(catalogUrl);
    }

    private void ReportFailed(RemoteConfigLoadError error)
    {
        Debug.LogError($"Remote config load failed. Type: {error.ErrorType}, Message: {error.Message}");
        Failed?.Invoke(error);
    }

    private void ReportCompleted()
    {
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
