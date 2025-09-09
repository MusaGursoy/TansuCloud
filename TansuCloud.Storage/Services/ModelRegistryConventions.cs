// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
namespace TansuCloud.Storage.Services;

/// <summary>
/// Non-invasive conventions for storing model artifacts in buckets.
/// These are metadata keys and folder naming hints only; no behavior changes.
/// </summary>
public static class ModelRegistryConventions
{
    // Suggested bucket or prefix name for model artifacts per tenant, e.g., models/{modelName}/{version}/
    public const string DefaultModelsPrefix = "models";

    // Object metadata keys to annotate artifacts for provenance and validation.
    public const string MetaModelName = "x-tansu-model-name";
    public const string MetaModelVersion = "x-tansu-model-version";
    public const string MetaFramework = "x-tansu-framework"; // e.g., ml.net, onnx, pytorch
    public const string MetaChecksum = "x-tansu-checksum"; // sha256 of content
    public const string MetaCreatedAt = "x-tansu-created-at"; // iso8601
    public const string MetaSource = "x-tansu-source"; // pipeline/job id or link
}
