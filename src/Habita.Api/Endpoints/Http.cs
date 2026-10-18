using Habita.Application.Members;

namespace Habita.Api.Endpoints;

public static class Http
{
    public static IResult ToResult<T>(this Result<T> r, Func<T, IResult>? ok = null) => r.Outcome switch
    {
        Outcome.Ok => ok is null ? Results.Ok(r.Value) : ok(r.Value!),
        Outcome.NotFound => Results.NotFound(), // also used for other organizations' records
        Outcome.Forbidden => Results.Problem(r.Error, statusCode: 403),
        Outcome.Conflict => Results.Problem(r.Error, statusCode: 409),
        Outcome.Unavailable => Results.Problem(r.Error, statusCode: 503),
        _ => Results.Problem(r.Error, statusCode: 400),
    };
}
