using Microsoft.AspNetCore.Http.HttpResults;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 管理 API 的错误响应（总设计 §10.1）：RFC 9457 ProblemDetails 加正确的状态码；业务错误另带稳定的 code 扩展字段，
/// 供前端按代码处理，不依赖面向人的标题文字。字段校验错误统一使用 ValidationProblem 的 errors 结构。
/// </summary>
internal static class ApiProblems
{
    public const string CodeExtension = "code";

    public static ProblemHttpResult Problem(int statusCode, string code, string title, string? detail = null) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?> { [CodeExtension] = code });

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) => TypedResults.ValidationProblem(errors);
}
