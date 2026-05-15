using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cocona;
using RepoScore.Data;
using RepoScore.Services;
using Spectre.Console;
using System.Globalization;

CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

void PrintValidationErrorsAndHelp(IEnumerable<string> errors)
{
    foreach (var error in errors)
    {
        Console.Error.WriteLine($"오류: {error}");
    }

    Console.Error.WriteLine();
    PrintHelpMessage();
}

void PrintHelpMessage()
{
    Console.Error.WriteLine("""
Usage: reposcore-cs [--token <String>] [--claims <String>] [--format <String>] [--output <String>] [--sort-by <String>] [--sort-order <String>] [--keywords <String>] [--no-cache] [--help] [--version] repos0 ... reposN

Arguments:
  0: repos    대상 저장소 목록 (예: owner/repo1 owner/repo2) (Required)

Options:
  -t, --token <String>     GitHub Token (미입력시 GITHUB_TOKEN 사용)
  --claims <String>        최근 이슈 선점 현황 조회 (issue|user)
  -f, --format <String>    출력 형식 (csv, txt, html) (Default: csv)
  -o, --output <String>    출력 디렉토리 경로 (Default: ./results)
  --sort-by <String>       정렬 기준 (score | id) (Default: score)
  --sort-order <String>    정렬 방법 (asc | desc) (Default: desc)
  --keywords <String>      이슈 선점 키워드 (쉼표 구분, 미입력시 기본값 사용)
  --no-cache               캐시를 무시하고 전체 데이터를 다시 수집할지 여부
  -h, --help               Show help message
  --version                Show version
""");
}

CoconaApp.Run((
[Argument(Description = "대상 저장소 목록 (예: owner/repo1 owner/repo2)")] string[] repos,
[Option('t', Description = "GitHub Token (미입력시 GITHUB_TOKEN 사용)")] string? token = null,
[Option(Description = "최근 이슈 선점 현황 조회")] ClaimsMode? claims = null,
[Option('f', Description = "출력 형식")] OutputFormat format = OutputFormat.Csv,
[Option('o', Description = "출력 디렉토리 경로")] string output = "./results",
[Option(Description = "정렬 기준")] SortBy sortBy = SortBy.Score,
[Option(Description = "정렬 방법")] SortOrder sortOrder = SortOrder.Desc,
[Option(Description = "이슈 선점 키워드 (쉼표 구분, 미입력시 기본값 사용)")] string? keywords = null,
[Option(Description = "캐시를 무시하고 전체 데이터를 다시 수집할지 여부")] bool noCache = false
) =>
{
    var exitCode = 0;
    var validationErrors = new List<string>();

    token ??= Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
    {
        validationErrors.Add("GitHub 토큰이 필요합니다. --token 옵션 또는 GITHUB_TOKEN 환경 변수를 설정해 주세요.");
    }

    if (repos.Length == 0)
    {
        validationErrors.Add("최소 1개 이상의 저장소를 입력해야 합니다. 예: owner/repo");
    }

    foreach (var repo in repos)
    {
        var parts = repo.Split('/');

        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            validationErrors.Add($"'{repo}'는 'owner/repo' 형식이 아닙니다.");
        }
    }

    if (validationErrors.Count > 0)
    {
        PrintValidationErrorsAndHelp(validationErrors);
        return 1;
    }

    string[]? parsedKeywords = keywords != null
        ? keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : null;

    // 전체 저장소 합산용 딕셔너리: 유저별 누적 기여 데이터
    var totalUserIssues = new Dictionary<string, List<IssueRecord>>();
    var totalUserPullRequests = new Dictionary<string, List<PRRecord>>();

    foreach (var repo in repos)
    {
        var parts = repo.Split('/');

        string ownerName = parts[0];
        string repoName = parts[1];

        string repoOutput = repos.Length > 1
            ? Path.Combine(output, $"{ownerName}_{repoName}")
            : output;
        if (!Directory.Exists(repoOutput)) Directory.CreateDirectory(repoOutput);
        string cachePath = Path.Combine(repoOutput, "cache.json");
        var cache = CacheManager.LoadCache(cachePath, repo, noCache);

        var service = new GitHubService(ownerName, repoName, token!, parsedKeywords);

        try
        {
            if (claims != null)
            {
                AnsiConsole.MarkupLine($"[[[blue]{ownerName}/{repoName}[/]]] 최근 이슈 선점 현황을 조회합니다...\n");

                var claimsData = service.GetRecentClaimsData();
                var report = ReportFormatter.BuildClaimsReport(claimsData, (ClaimsMode)claims);
                Console.Write(report);
                continue;
            }

            AnsiConsole.MarkupLine($"[yellow]{repo}[/] 기여자 데이터 수집 및 분석 중...");

            if (!Directory.Exists(repoOutput)) Directory.CreateDirectory(repoOutput);

            if (!CacheManager.HasSameKeywords(cache, parsedKeywords))
            {
                Console.Error.WriteLine("키워드 옵션이 이전 실행과 달라 캐시를 무효화합니다.");

                cache = new RepoCache
                {
                    Repository = repo,
                    Keywords = parsedKeywords
                };
            }

            DateTimeOffset? since = cache.LastAnalyzedAt == DateTimeOffset.MinValue
                ? null
                : cache.LastAnalyzedAt;

            if (since.HasValue)
            {
                Console.Error.WriteLine($"기존 캐시 존재: {since.Value.ToLocalTime():yyyy-MM-dd HH:mm}");
            }
            else
            {
                Console.Error.WriteLine("기존 캐시 없음: 전체 데이터를 수집합니다.");
            }

            // 모든 PR 데이터를 한 번에 가져와서 루프 내에서 필터링 (N+1 최적화)
            var allNewPrs = service.GetPullRequests(since);
            var allNewIssues = service.GetIssues(since);

            // 새로 조회된 PR과 이슈의 작성자, 기존 캐시의 작성자 목록을 통합하여 유니크한 기여자 목록 생성
            List<string> contributors = allNewPrs.Select(p => p.AuthorLogin)
                .Concat(allNewIssues.Select(i => i.AuthorLogin))
                .Concat(cache.UserIssues.Keys)
                .Concat(cache.UserPullRequests.Keys)
                .Where(login => !string.IsNullOrEmpty(login))
                .Distinct()
                .ToList();

            if (contributors.Count == 0) { Console.Error.WriteLine("조회된 기여자가 없습니다."); continue; }

            var reportData = new List<(string Id, int docIssues, int featBugIssues, int typoPrs, int docPrs, int featBugPrs, int Score)>();

            foreach (var user in contributors)
            {
                var newIssues = allNewIssues.Where(i => i.AuthorLogin == user).ToList();
                var newPrs = allNewPrs.Where(p => p.AuthorLogin == user).ToList();

                if (!cache.UserIssues.ContainsKey(user)) cache.UserIssues[user] = new List<IssueRecord>();
                if (!cache.UserPullRequests.ContainsKey(user)) cache.UserPullRequests[user] = new List<PRRecord>();

                foreach (var ni in newIssues)
                {
                    int index = cache.UserIssues[user].FindIndex(c => c.Number == ni.Number);
                    if (index >= 0) cache.UserIssues[user][index] = ni;
                    else cache.UserIssues[user].Add(ni);
                }

                foreach (var npr in newPrs)
                {
                    int index = cache.UserPullRequests[user].FindIndex(p => p.Number == npr.Number);
                    if (index >= 0) cache.UserPullRequests[user][index] = npr;
                    else cache.UserPullRequests[user].Add(npr);
                }

                var userIssuesToCalc = cache.UserIssues[user];
                var prsToCalc = cache.UserPullRequests[user];

                var featureBugPrs = prsToCalc.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Bug) || p.Labels.Contains(GitHubIssuePrLabel.Enhancement)).ToList();
                var docPrs = prsToCalc.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Documentation)).ToList();
                var typoPrs = prsToCalc.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Typo)).ToList();
                var featureBugIssues = userIssuesToCalc.Where(c => c.Labels.Contains(GitHubIssuePrLabel.Bug) || c.Labels.Contains(GitHubIssuePrLabel.Enhancement)).ToList();
                var docIssues = userIssuesToCalc.Where(c => c.Labels.Contains(GitHubIssuePrLabel.Documentation)).ToList();

                int finalScore
                    = ScoreCalculator.CalculateFinalScore(featureBugPrs.Count, docPrs.Count, typoPrs.Count, featureBugIssues.Count, docIssues.Count);

                reportData.Add((user, docIssues.Count, featureBugIssues.Count, typoPrs.Count, docPrs.Count, featureBugPrs.Count, finalScore));

                // 전체 합산용 누적
                if (repos.Length > 1)
                {
                    if (!totalUserIssues.ContainsKey(user)) totalUserIssues[user] = new List<IssueRecord>();
                    if (!totalUserPullRequests.ContainsKey(user)) totalUserPullRequests[user] = new List<PRRecord>();

                    // 저장소별 이슈/PR은 번호가 겹칠 수 있으므로 URL 기준으로 중복 방지
                    // URL이 비어있는 경우를 대비해 URL이 있으면 URL로, 없으면 Number 기준으로 판별
                    foreach (var issue in cache.UserIssues[user])
                    {
                        bool isDuplicate = string.IsNullOrEmpty(issue.Url)
                            ? totalUserIssues[user].Any(i => string.IsNullOrEmpty(i.Url) && i.Number == issue.Number)
                            : totalUserIssues[user].Any(i => i.Url == issue.Url);
                        if (!isDuplicate)
                            totalUserIssues[user].Add(issue);
                    }
                    foreach (var pr in cache.UserPullRequests[user])
                    {
                        bool isDuplicate = string.IsNullOrEmpty(pr.Url)
                            ? totalUserPullRequests[user].Any(p => string.IsNullOrEmpty(p.Url) && p.Number == pr.Number)
                            : totalUserPullRequests[user].Any(p => p.Url == pr.Url);
                        if (!isDuplicate)
                            totalUserPullRequests[user].Add(pr);
                    }
                }
            }

            CacheManager.SaveCache(cachePath, cache, parsedKeywords);
            Console.Error.WriteLine($"캐시 갱신 및 저장 완료: {cachePath}");

            reportData = ReportSorter.SortReportData(reportData, sortBy, sortOrder);

            // CSV 데이터 파일 생성
            var csv = new StringBuilder();
            csv.AppendLine("아이디, 문서이슈, 버그/기능이슈, 오타PR, 문서PR, 버그/기능PR, 총점");
            foreach (var r in reportData) csv.AppendLine($"{r.Id}, {r.docIssues}, {r.featBugIssues}, {r.typoPrs}, {r.docPrs}, {r.featBugPrs}, {r.Score}");

            string csvPath = Path.Combine(repoOutput, "results.csv");
            File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);
            Console.Error.WriteLine($"기본 데이터(CSV) 저장 완료: {csvPath}");

            // TXT 리포트 생성
            if (format == OutputFormat.Txt)
            {
                string txtPath = Path.Combine(repoOutput, "results.txt");
                string txtContent = ReportFormatter.BuildTextReport(repo, reportData);
                File.WriteAllText(txtPath, txtContent, Encoding.UTF8);
                Console.Error.WriteLine($"가독성 리포트(TXT) 추가 저장 완료: {txtPath}");
            }

            // HTML 리포트 생성
            if (format == OutputFormat.Html)
            {
                string htmlPath = Path.Combine(repoOutput, "results.html");
                string htmlContent = ReportFormatter.BuildHtmlReport(repo, reportData);
                File.WriteAllText(htmlPath, htmlContent, Encoding.UTF8);
                Console.Error.WriteLine($"HTML 리포트 추가 저장 완료: {htmlPath}");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            exitCode = 1;
        }
    }

    // 저장소가 1개 이상일 때 전체 합산 리포트 생성
    if (repos.Length > 1 && (totalUserIssues.Count > 0 || totalUserPullRequests.Count > 0))
    {
        try
        {
            AnsiConsole.MarkupLine($"\n[green]전체 저장소 합산 리포트 생성 중...[/]");

            var totalReportData = new List<(string Id, int docIssues, int featBugIssues, int typoPrs, int docPrs, int featBugPrs, int Score)>();

            var allUsers = totalUserIssues.Keys.Union(totalUserPullRequests.Keys).ToList();

            foreach (var user in allUsers)
            {
                var allIssues = totalUserIssues.TryGetValue(user, out var issues) ? issues : new List<IssueRecord>();
                var allPrs = totalUserPullRequests.TryGetValue(user, out var prs) ? prs : new List<PRRecord>();

                var featureBugPrs = allPrs.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Bug) || p.Labels.Contains(GitHubIssuePrLabel.Enhancement)).ToList();
                var docPrs = allPrs.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Documentation)).ToList();
                var typoPrs = allPrs.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Typo)).ToList();
                var featureBugIssues = allIssues.Where(c => c.Labels.Contains(GitHubIssuePrLabel.Bug) || c.Labels.Contains(GitHubIssuePrLabel.Enhancement)).ToList();
                var docIssues = allIssues.Where(c => c.Labels.Contains(GitHubIssuePrLabel.Documentation)).ToList();

                int finalScore = ScoreCalculator.CalculateFinalScore(featureBugPrs.Count, docPrs.Count, typoPrs.Count, featureBugIssues.Count, docIssues.Count);

                totalReportData.Add((user, docIssues.Count, featureBugIssues.Count, typoPrs.Count, docPrs.Count, featureBugPrs.Count, finalScore));
            }

            totalReportData = ReportSorter.SortReportData(totalReportData, sortBy, sortOrder);

            string totalOutput = output;
            if (!Directory.Exists(totalOutput)) Directory.CreateDirectory(totalOutput);

            // 합산 CSV
            var totalCsv = new StringBuilder();
            totalCsv.AppendLine("아이디, 문서이슈, 버그/기능이슈, 오타PR, 문서PR, 버그/기능PR, 총점");
            foreach (var r in totalReportData) totalCsv.AppendLine($"{r.Id}, {r.docIssues}, {r.featBugIssues}, {r.typoPrs}, {r.docPrs}, {r.featBugPrs}, {r.Score}");

            string totalCsvPath = Path.Combine(totalOutput, "results.csv");
            File.WriteAllText(totalCsvPath, totalCsv.ToString(), Encoding.UTF8);
            Console.Error.WriteLine($"전체 합산 데이터(CSV) 저장 완료: {totalCsvPath}");

            // 합산 TXT
            if (format == OutputFormat.Txt)
            {
                string totalLabel = string.Join(" + ", repos);
                string totalTxtPath = Path.Combine(totalOutput, "results.txt");
                string totalTxtContent = ReportFormatter.BuildTextReport(totalLabel, totalReportData);
                File.WriteAllText(totalTxtPath, totalTxtContent, Encoding.UTF8);
                Console.Error.WriteLine($"전체 합산 리포트(TXT) 저장 완료: {totalTxtPath}");
            }

            // 합산 HTML
            if (format == OutputFormat.Html)
            {
                string totalLabel = string.Join(" + ", repos);
                string totalHtmlPath = Path.Combine(totalOutput, "results.html");
                string totalHtmlContent = ReportFormatter.BuildHtmlReport(totalLabel, totalReportData);
                File.WriteAllText(totalHtmlPath, totalHtmlContent, Encoding.UTF8);
                Console.Error.WriteLine($"전체 합산 HTML 리포트 저장 완료: {totalHtmlPath}");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            exitCode = 1;
        }
    }

    // 모든 점수 계산 및 리포트 출력이 끝난 후, 이슈 선점 현황을 일괄 조회 및 출력
    if (claims != null)
    {
        foreach (var repo in repos)
        {
            var parts = repo.Split('/');
            if (parts.Length != 2) continue; // 포맷 오류는 위쪽 점수 계산 루프에서 이미 경고됨

            string ownerName = parts[0];
            string repoName = parts[1];
            var service = new GitHubService(ownerName, repoName, token!, parsedKeywords);

            try
            {
                AnsiConsole.MarkupLine($"\n[[[blue]{ownerName}/{repoName}[/]]] 최근 이슈 선점 현황을 조회합니다...\n");
                var claimsData = service.GetRecentClaimsData();
                var report = ReportFormatter.BuildClaimsReport(claimsData, claims.GetValueOrDefault(ClaimsMode.Issue));
                Console.Write(report);
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                exitCode = 1;
            }
        }
    }
    return exitCode;
});
