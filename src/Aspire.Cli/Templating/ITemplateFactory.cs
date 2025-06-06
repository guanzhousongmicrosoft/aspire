// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

internal interface ITemplateFactory
{
    IEnumerable<ITemplate> GetTemplates();
}