using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace NetSonar.Avalonia.Settings;

public abstract class RootSettingsFile<T> : SubSettings, IDisposable where T : RootSettingsFile<T>, new()
{
    /// <summary>
    /// The lazy-loaded singleton instance of the settings class.
    /// </summary>
    private static readonly Lazy<T> _instance = new(LoadOrCreate);

    /// <summary>
    /// The global singleton instance of the settings class.
    /// </summary>
    public static T Instance => _instance.Value;

    /// <summary>
    /// Gets a value indicating whether the singleton instance has been created.
    /// </summary>
    public static bool IsInstanceCreated => _instance.IsValueCreated;

    #region Members

    private Timer? _saveTimer;
    private readonly Lock _saveLock = new();

    #endregion

    #region Properties
    /// <summary>
    /// Gets the default options used for JSON serialization within this class.
    /// </summary>
    /// <remarks>This property provides access to the application's configured <see
    /// cref="JsonSerializerOptions"/> instance. Use these options to ensure consistent serialization and
    /// deserialization behavior across the application.</remarks>
    [JsonIgnore]
    protected virtual JsonSerializerOptions JsonOptions => App.JsonSerializerOptions;

    /// <summary>
    /// Gets the file system path to the application's configuration directory.
    /// </summary>
    [JsonIgnore]
    public virtual string DirectoryPath => App.ConfigPath;

    /// <summary>
    /// Gets the file name of this settings file.
    /// </summary>
    [JsonIgnore]
    public abstract string FileName { get; }

    /// <summary>
    /// Gets the full file system path to the configuration file.
    /// </summary>
    [JsonIgnore]
    public string FilePath => Path.Combine(DirectoryPath, FileName);

    /// <summary>
    /// Gets a value indicating whether the current state allows the object to be saved.
    /// </summary>
    [JsonIgnore]
    public bool CanSave { get; protected set; }

    /// <summary>
    /// Gets the default debounce interval, in milliseconds, used to delay save operations.
    /// </summary>
    /// <remarks>This value determines the minimum time to wait before triggering a save after changes occur.
    /// Adjusting the debounce interval can help optimize performance by reducing the frequency of save operations in
    /// scenarios with rapid consecutive changes.<br/>
    /// 0 = Instant save without debounce.</remarks>
    [JsonIgnore]
    protected virtual int DefaultDebounceSaveMilliseconds => 1000;
    #endregion

    /// <summary>
    /// Loads settings from the configured file location. If the file doesn't exist, returns a new instance.
    /// </summary>
    private static T LoadOrCreate()
    {
        var instance = new T();
        var filePath = instance.FilePath;
        var loadFromFile = false;

        try
        {
            if (File.Exists(filePath))
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);

                var loaded = JsonSerializer.Deserialize<T>(stream, instance.JsonOptions);

                if (loaded is not null)
                {
                    instance = loaded;
                    loadFromFile = true;
                }
            }
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, $"LoadOrCreate {instance.FileName}");
        }

        instance.CanSave = true;
        instance.OnLoaded(loadFromFile);
        return instance;
    }

    /// <summary>
    /// Executes custom logic before the object is saved to persistent storage. Override this method to perform
    /// validation, transformation, or other pre-save operations.
    /// </summary>
    /// <remarks>This method is called automatically during the save process. Derived classes can override it
    /// to implement application-specific behavior prior to saving. The base implementation does not perform any
    /// actions.</remarks>
    protected virtual void BeforeSave()
    {

    }

    /// <summary>
    /// Saves the current settings to the configured file location using JSON serialization.
    /// </summary>
    /// <remarks>If the target directory does not exist, it is created automatically. Any exceptions
    /// encountered during the save operation are handled internally and do not propagate to the caller.</remarks>
    public void Save()
    {
        lock (_saveLock)
        {
            if (!CanSave) return;
            CanSave = false;
            try
            {
                Directory.CreateDirectory(App.ConfigPath);

                BeforeSave();

                // Use FileOptions for better performance
                using var stream = new FileStream(
                    FilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough);

                JsonSerializer.Serialize(stream, this, GetType(), JsonOptions);
                AfterSave();
            }
            catch (Exception e)
            {
                App.HandleSafeException(e, $"Save {FileName}");
            }
            CanSave = true;
        }
    }

    /// <summary>
    /// Saves the settings after a debounce delay. Multiple rapid calls will reset the timer.
    /// </summary>
    /// <param name="debounceMilliseconds">The delay in milliseconds before saving. Default is <see cref="DefaultDebounceSaveMilliseconds"/>.</param>
    public void DebouncedSave(int debounceMilliseconds = 0)
    {
        if (!CanSave) return;

        if (debounceMilliseconds <= 0)
        {
            if (DefaultDebounceSaveMilliseconds <= 0)
            {
                Save();
                return;
            }
            debounceMilliseconds = DefaultDebounceSaveMilliseconds;
        }

        if (_saveTimer is null)
        {
            _saveTimer = new Timer(_ => Save(), null, debounceMilliseconds, Timeout.Infinite);
        }
        else
        {
            _saveTimer.Change(debounceMilliseconds, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Cancels any pending debounced save operation.
    /// </summary>
    public void CancelDebouncedSave()
    {
        lock (_saveLock)
        {
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Saves the current settings to the configured file location using JSON serialization.
    /// </summary>
    /// <remarks>If the target directory does not exist, it is created automatically. Any exceptions
    /// encountered during the save operation are handled internally and do not propagate to the caller.</remarks>
    public static void SaveInstance()
    {
        if (!IsInstanceCreated) return;
        Instance.Save();
    }

    /// <summary>
    /// Executes custom logic after the object is saved to persistent storage. Override this method to perform
    /// validation, transformation, or other pre-save operations.
    /// </summary>
    /// <remarks>This method is called automatically during the save process. Derived classes can override it
    /// to implement application-specific behavior after to saving. The base implementation does not perform any
    /// actions.</remarks>
    protected virtual void AfterSave()
    {

    }

    /// <summary>
    /// Deletes the settings file if it exists.
    /// </summary>
    public void DeleteFile()
    {
        try
        {
            if (FileExists())
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, $"Delete {FileName}");
        }
    }

    /// <summary>
    /// Checks if the settings file exists.
    /// </summary>
    public bool FileExists() => File.Exists(FilePath);

    /// <inheritdoc />
    public virtual void Dispose()
    {
        _saveTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}