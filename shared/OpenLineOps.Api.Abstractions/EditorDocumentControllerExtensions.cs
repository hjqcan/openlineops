using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace OpenLineOps.Api.Abstractions;

public static class EditorDocumentControllerExtensions
{
    public static void SetEditorDocumentRevision(this HttpResponse response, string revision)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = EditorDocumentConcurrency.ToEntityTag(revision);
    }

    public static ObjectResult EditorDocumentPreconditionProblem(
        this ControllerBase controller,
        EditorDocumentPrecondition precondition,
        string currentRevision)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var (status, title, detail) = precondition switch
        {
            EditorDocumentPrecondition.Missing => (
                StatusCodes.Status428PreconditionRequired,
                "Editor.DocumentRevisionRequired",
                "The If-Match header containing the revision that was loaded by the editor is required."),
            EditorDocumentPrecondition.Stale => (
                StatusCodes.Status412PreconditionFailed,
                "Editor.DocumentRevisionConflict",
                "The document changed after it was loaded. Reload it or use the explicit overwrite contract."),
            EditorDocumentPrecondition.ForceNotExplicit => (
                StatusCodes.Status400BadRequest,
                "Editor.ExplicitOverwriteRequired",
                $"If-Match: * is accepted only with {EditorDocumentConcurrency.ConflictResolutionHeaderName}: {EditorDocumentConcurrency.ExplicitOverwriteToken}."),
            _ => throw new ArgumentOutOfRangeException(nameof(precondition), precondition, null)
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };
        problem.Extensions["currentRevision"] = currentRevision;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
