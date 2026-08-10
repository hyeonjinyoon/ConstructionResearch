using System.Text.Json.Serialization;

namespace ConstructionSurvey.Models;

public class SurveySubmission
{
    /// <summary>저장된 JSON 파일 이름(확장자 제외). 파일 내용에는 기록되지 않습니다.</summary>
    [JsonIgnore]
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Trade { get; set; } = string.Empty;
    public string AccessTime { get; set; } = string.Empty;
    public string SubmitTime { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public Dictionary<int, int> Answers { get; set; } = new();
    public int TotalScore { get; set; }
    public string RiskLevel { get; set; } = string.Empty;
    public string CriticalFlags { get; set; } = string.Empty;
}
