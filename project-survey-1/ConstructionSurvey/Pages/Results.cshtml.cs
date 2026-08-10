using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ConstructionSurvey.Models;
using ConstructionSurvey.Services;

namespace ConstructionSurvey.Pages;

public class ResultsModel : PageModel
{
    private readonly JsonResultService _jsonService;
    private readonly SurveyDataService _dataService;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public List<SurveySubmission> Submissions { get; set; } = new();
    public int TotalCount { get; set; }
    public int GreenCount { get; set; }
    public int YellowCount { get; set; }
    public int RedCount { get; set; }

    /// <summary>문항 상세 모달에서 사용할 문항 목록(JSON)</summary>
    public string QuestionsJson { get; set; } = "[]";

    /// <summary>문항 상세 모달에서 사용할 제출 목록(JSON). 테이블 행 순서와 동일</summary>
    public string SubmissionsJson { get; set; } = "[]";

    /// <summary>해당 날짜에 제출된 데이터만 표시</summary>
    public List<string> AvailableDates { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Filter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Date { get; set; }

    /// <summary>삭제 후 안내 문구 (리다이렉트 후 1회만 표시)</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    public ResultsModel(JsonResultService jsonService, SurveyDataService dataService)
    {
        _jsonService = jsonService;
        _dataService = dataService;
    }

    public void OnGet()
    {
        var allSubmissions = _jsonService.GetAllSubmissions();

        // 날짜 목록 추출 (SubmitTime 형식: "2026-03-27 19:17:51")
        AvailableDates = allSubmissions
            .Select(s => s.SubmitTime?.Split(' ').FirstOrDefault() ?? "")
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        // 날짜 필터 적용
        var dateFiltered = allSubmissions;
        if (!string.IsNullOrEmpty(Date))
        {
            dateFiltered = allSubmissions
                .Where(s => s.SubmitTime != null && s.SubmitTime.StartsWith(Date))
                .ToList();
        }

        // 통계는 날짜 필터 기준
        TotalCount = dateFiltered.Count;
        GreenCount = dateFiltered.Count(s => s.RiskLevel == "양호");
        YellowCount = dateFiltered.Count(s => s.RiskLevel == "주의");
        RedCount = dateFiltered.Count(s => s.RiskLevel == "위험");

        // 위험등급 필터 적용
        if (!string.IsNullOrEmpty(Filter))
        {
            Submissions = dateFiltered.Where(s => s.RiskLevel == Filter).ToList();
        }
        else
        {
            Submissions = dateFiltered;
        }

        BuildDetailJson();
    }

    /// <summary>사람별 응답 상세 모달에 넘길 데이터를 JSON으로 만든다</summary>
    private void BuildDetailJson()
    {
        var questions = _dataService.GetQuestions()
            .Select(q => new
            {
                number = q.Number,
                category = q.Category,
                text = q.Text,
                reverse = q.IsReverseScored,
                alcohol = q.IsAlcoholQuestion
            })
            .ToList();

        var rows = Submissions
            .Select(s => new
            {
                name = s.Name,
                company = s.Company,
                trade = s.Trade,
                submitTime = s.SubmitTime,
                totalScore = s.TotalScore,
                riskLevel = s.RiskLevel,
                answers = s.Answers ?? new Dictionary<int, int>()
            })
            .ToList();

        QuestionsJson = JsonSerializer.Serialize(questions, _jsonOptions);
        SubmissionsJson = JsonSerializer.Serialize(rows, _jsonOptions);
    }

    /// <summary>응답 1건 삭제 (표의 삭제 버튼)</summary>
    public IActionResult OnPostDelete(string id)
    {
        StatusMessage = _jsonService.DeleteSubmission(id)
            ? "응답 1건을 삭제했습니다."
            : "삭제하지 못했습니다. 이미 삭제된 응답일 수 있습니다.";

        return RedirectKeepingFilters();
    }

    /// <summary>체크한 응답을 한 번에 삭제</summary>
    public IActionResult OnPostDeleteSelected(string[] ids)
    {
        if (ids == null || ids.Length == 0)
        {
            StatusMessage = "선택된 응답이 없습니다.";
            return RedirectKeepingFilters();
        }

        var deleted = _jsonService.DeleteSubmissions(ids);
        StatusMessage = deleted == ids.Length
            ? $"응답 {deleted}건을 삭제했습니다."
            : $"응답 {deleted}건을 삭제했습니다. ({ids.Length - deleted}건은 이미 삭제된 응답입니다)";

        return RedirectKeepingFilters();
    }

    /// <summary>삭제 후 보고 있던 날짜/등급 필터를 그대로 유지한 채 목록으로 돌아갑니다.</summary>
    private IActionResult RedirectKeepingFilters()
    {
        var routeValues = new Dictionary<string, string?>();

        if (!string.IsNullOrEmpty(Filter))
            routeValues["Filter"] = Filter;

        if (!string.IsNullOrEmpty(Date))
            routeValues["Date"] = Date;

        return RedirectToPage("/Results", routeValues);
    }
}
