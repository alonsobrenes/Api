using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;

namespace EPApi.Infrastructure
{
    public class SqlExceptionFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            if (context.Exception is SqlException sqlEx)
            {
                if (SqlErrorHelper.IsUniqueViolation(sqlEx))
                {
                    var problem = new ProblemDetails
                    {
                        Title = "Conflict",
                        Detail = SqlErrorHelper.GetFriendlyUniqueMessage(sqlEx),
                        Status = StatusCodes.Status409Conflict
                    };
                    context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
                    context.ExceptionHandled = true;
                    return;
                }
            }
        }
    }
}
