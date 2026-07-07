using System.Threading;
using System.Threading.Tasks;

namespace RapidTransitMod.RailTravel
{
    internal sealed class Runner
    {
        private readonly Calculator m_Calculator;

        public Runner(Calculator calculator = null)
        {
            m_Calculator = calculator ?? new Calculator();
        }

        public Task<Result> CalculateAsync(
            Request request,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return m_Calculator.Calculate(request, cancellationToken);
                },
                cancellationToken);
        }
    }
}
