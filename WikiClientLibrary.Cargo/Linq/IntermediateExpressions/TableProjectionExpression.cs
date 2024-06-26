﻿using System.Diagnostics;

namespace WikiClientLibrary.Cargo.Linq.IntermediateExpressions;

/// <summary>
/// Represents a table name and its alias as specified in SQL <c>WHERE</c> clause.
/// </summary>
internal class TableProjectionExpression : CargoSqlExpression
{

    public TableProjectionExpression(CargoModel model, string tableAlias)
    {
        Debug.Assert(model != null);
        Debug.Assert(!string.IsNullOrEmpty(tableAlias));
        Model = model;
        TableAlias = tableAlias;
    }

    public CargoModel Model { get; }

    public string TableName => Model.Name;

    public string TableAlias { get; }

    /// <inheritdoc />
    public override string ToString() => $"{TableName} AS {TableAlias}";

}
