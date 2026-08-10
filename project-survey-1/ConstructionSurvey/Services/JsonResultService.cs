using System.Text.Encodings.Web;
using System.Text.Json;
using ConstructionSurvey.Models;

namespace ConstructionSurvey.Services;

public class JsonResultService
{
    private readonly string _resultsPath;
    private readonly string _deletedPath;
    private readonly ILogger<JsonResultService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>중복 확인과 저장 사이에 다른 요청이 끼어들지 못하게 막는 잠금</summary>
    private readonly object _saveLock = new();

    public JsonResultService(IWebHostEnvironment env, ILogger<JsonResultService> logger)
    {
        _resultsPath = Path.Combine(env.ContentRootPath, "Results");
        // 삭제한 응답은 지워지지 않고 이 폴더로 옮겨져 보관됩니다.
        _deletedPath = Path.Combine(_resultsPath, "Deleted");
        _logger = logger;
        Directory.CreateDirectory(_resultsPath);
        Directory.CreateDirectory(_deletedPath);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    /// <summary>
    /// 결과를 저장하고 저장된 파일 id를 돌려줍니다.
    /// 같은 사람이 같은 접속(AccessTime)에서 이미 제출한 기록이 있으면 새로 저장하지 않고
    /// 기존 기록의 id를 돌려줍니다. 제출 버튼을 두 번 눌렀을 때 같은 응답이 두 건 쌓이는 것을 막습니다.
    /// </summary>
    public string? SaveSubmission(SurveySubmission submission)
    {
        // 두 번의 제출이 거의 동시에 도착해도 한 건만 저장되도록 잠급니다.
        lock (_saveLock)
        {
            var duplicateId = FindDuplicateId(submission);
            if (duplicateId != null)
            {
                _logger.LogInformation(
                    "Duplicate submission ignored for {Name}, returning existing: {Id}",
                    submission.Name, duplicateId);
                return duplicateId;
            }

            try
            {
                var safeName = SanitizeFileName(submission.Name);
                var guid = Guid.NewGuid().ToString("N")[..4];
                var fileName = $"{DateTime.Now:yyyyMMdd}_{DateTime.Now:HHmmss}_{safeName}_{guid}.json";
                var filePath = Path.Combine(_resultsPath, fileName);

                var json = JsonSerializer.Serialize(submission, _jsonOptions);
                File.WriteAllText(filePath, json);

                _logger.LogInformation("Survey result saved: {FileName}", fileName);
                return Path.GetFileNameWithoutExtension(fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save survey result for {Name}", submission.Name);
                return null;
            }
        }
    }

    /// <summary>
    /// 완전히 똑같은 응답이 이미 저장되어 있으면 그 id를 돌려줍니다.
    /// 접속시간은 설문 페이지를 연 시각이라, 이름·업체·공종·접속시간이 모두 같고
    /// 14문항 답까지 같으면 같은 화면에서 두 번 제출된 같은 응답입니다.
    /// 답이 하나라도 다르면 별개의 응답으로 보고 그대로 저장합니다.
    /// (같은 날 저장된 파일만 확인합니다. 이중 제출은 몇 초 안에 일어나므로 충분합니다)
    /// </summary>
    private string? FindDuplicateId(SurveySubmission submission)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(submission.AccessTime))
                return null;

            // 파일 이름 형식: yyyyMMdd_HHmmss_이름_guid.json
            var pattern = $"{DateTime.Now:yyyyMMdd}_*_{SanitizeFileName(submission.Name)}_*.json";

            foreach (var file in Directory.GetFiles(_resultsPath, pattern))
            {
                var json = File.ReadAllText(file);
                var existing = JsonSerializer.Deserialize<SurveySubmission>(json, _jsonOptions);

                if (existing != null
                    && existing.AccessTime == submission.AccessTime
                    && existing.Name == submission.Name
                    && existing.Company == submission.Company
                    && existing.Trade == submission.Trade
                    && SameAnswers(existing.Answers, submission.Answers))
                {
                    return Path.GetFileNameWithoutExtension(file);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            // 중복 확인에 실패해도 저장 자체는 막지 않습니다.
            _logger.LogWarning(ex, "Failed to check duplicate submission for {Name}", submission.Name);
            return null;
        }
    }

    public SurveySubmission? GetSubmission(string id)
    {
        try
        {
            if (!TryGetResultPath(id, out var filePath))
                return null;

            if (!File.Exists(filePath))
                return null;

            var json = File.ReadAllText(filePath);
            var submission = JsonSerializer.Deserialize<SurveySubmission>(json, _jsonOptions);
            if (submission != null)
                submission.Id = id;
            return submission;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read survey file: {Id}", id);
            return null;
        }
    }

    public List<SurveySubmission> GetAllSubmissions()
    {
        var submissions = new List<SurveySubmission>();

        if (!Directory.Exists(_resultsPath))
            return submissions;

        var files = Directory.GetFiles(_resultsPath, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var submission = JsonSerializer.Deserialize<SurveySubmission>(json, _jsonOptions);
                if (submission != null)
                {
                    submission.Id = Path.GetFileNameWithoutExtension(file);
                    submissions.Add(submission);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read survey file: {File}", Path.GetFileName(file));
            }
        }

        // 제출시간 기준 내림차순 정렬
        submissions.Sort((a, b) => string.Compare(b.SubmitTime, a.SubmitTime, StringComparison.Ordinal));

        return submissions;
    }

    /// <summary>문항 답이 전부 같은지 비교합니다.</summary>
    private static bool SameAnswers(Dictionary<int, int> a, Dictionary<int, int> b)
    {
        if (a.Count != b.Count)
            return false;

        foreach (var answer in a)
        {
            if (!b.TryGetValue(answer.Key, out var value) || value != answer.Value)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 결과 목록에서 응답 1건을 제외합니다.
    /// 파일을 지우지 않고 Results/Deleted 폴더로 옮기므로, 잘못 지웠을 때 되돌릴 수 있습니다.
    /// </summary>
    public bool DeleteSubmission(string id)
    {
        try
        {
            if (!TryGetResultPath(id, out var filePath))
                return false;

            if (!File.Exists(filePath))
                return false;

            // 보관 폴더에 같은 이름이 있으면 덮어쓰지 않고 뒤에 번호를 붙입니다.
            var targetPath = Path.Combine(_deletedPath, id + ".json");
            var counter = 1;
            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(_deletedPath, $"{id}_{counter}.json");
                counter++;
            }

            File.Move(filePath, targetPath);

            _logger.LogInformation("Survey result moved to Deleted: {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete survey result: {Id}", id);
            return false;
        }
    }

    /// <summary>여러 건을 한 번에 삭제하고, 실제로 삭제된 건수를 돌려줍니다.</summary>
    public int DeleteSubmissions(IEnumerable<string> ids)
    {
        return ids.Count(DeleteSubmission);
    }

    /// <summary>
    /// id(= 파일 이름)가 Results 폴더 바로 아래의 정상적인 파일 이름인지 확인합니다.
    /// "../" 같은 값으로 다른 폴더의 파일을 건드리지 못하게 막습니다.
    /// </summary>
    private bool TryGetResultPath(string id, out string filePath)
    {
        filePath = string.Empty;

        if (string.IsNullOrWhiteSpace(id))
            return false;

        var fileName = id + ".json";
        if (fileName != Path.GetFileName(fileName))
            return false;

        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        filePath = Path.Combine(_resultsPath, fileName);
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = name;
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return sanitized;
    }
}
