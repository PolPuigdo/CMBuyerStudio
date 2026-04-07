using CMBuyerStudio.Application.RunAnalysis;
using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Application.Abstractions
{
    public interface IRunAnalysisService
    {
        Task RunAsync(IProgress<RunProgressEvent> progress, CancellationToken cancellationToken);
    }
}
