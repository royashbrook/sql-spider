-- ring 2 (continued): the scalar UDF SalesOrderHeader's computed SalesOrderNumber calls.
-- The function-call edge put ufnLeadingZeros on the frontier as referenced-but-undefined;
-- pulling its module definition (sys.sql_modules, same query that pulls procs) closes it.
-- Definition from Microsoft's public AdventureWorks sample database.
CREATE FUNCTION [dbo].[ufnLeadingZeros](
    @Value int
)
RETURNS varchar(8)
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @ReturnValue varchar(8);

    SET @ReturnValue = CONVERT(varchar(8), @Value);
    SET @ReturnValue = REPLICATE('0', 8 - DATALENGTH(@ReturnValue)) + @ReturnValue;

    RETURN (@ReturnValue);
END;
