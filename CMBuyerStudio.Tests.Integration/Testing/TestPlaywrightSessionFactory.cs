using System.Reflection;
using System.Xml.Linq;
using CMBuyerStudio.Infrastructure.Cardmarket.Playwright;
using Microsoft.Playwright;

namespace CMBuyerStudio.Tests.Integration.Testing;

public sealed class TestPlaywrightSessionFactory : IPlaywrightSessionFactory
{
    private readonly Func<Uri, TestRouteResponse?> _responseFactory;

    public TestPlaywrightSessionFactory(Func<Uri, TestRouteResponse?> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public Task<PlaywrightSession> CreateChromiumAsync(bool headless = false, Proxy? proxy = null)
    {
        return Task.FromResult(CreateSession());
    }

    public Task<PlaywrightSession> CreateFirefoxAsync(bool headless = false, Proxy? proxy = null)
    {
        return Task.FromResult(CreateSession());
    }

    private PlaywrightSession CreateSession()
    {
        var pageState = new FakePageState(_responseFactory);
        var page = DynamicProxy<IPage>.Create((method, args) => pageState.Invoke(method, args));
        pageState.AttachPage(page);

        var browserContext = DynamicProxy<IBrowserContext>.Create((method, args) => DisposeAsyncOrDefault(method));
        var browser = DynamicProxy<IBrowser>.Create((method, args) => DisposeAsyncOrDefault(method));
        var playwright = DynamicProxy<IPlaywright>.Create((method, args) => method.Name == nameof(IDisposable.Dispose) ? null : throw new NotSupportedException(method.Name));

        return new PlaywrightSession(playwright, browser, browserContext, page);
    }

    private static object? DisposeAsyncOrDefault(MethodInfo method)
    {
        return method.Name switch
        {
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
    }

    private sealed class FakePageState
    {
        private readonly Func<Uri, TestRouteResponse?> _responseFactory;
        private XElement _root = new("root");
        private IPage? _page;

        public FakePageState(Func<Uri, TestRouteResponse?> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public string Url { get; private set; } = string.Empty;

        public void AttachPage(IPage page)
        {
            _page = page;
        }

        public object? Invoke(MethodInfo method, object?[]? args)
        {
            return method.Name switch
            {
                nameof(IPage.GotoAsync) => GotoAsync((string)args![0]!),
                nameof(IPage.WaitForTimeoutAsync) => Task.CompletedTask,
                nameof(IPage.Locator) => CreateLocator(Select(_root, (string)args![0]!)),
                "get_Url" => Url,
                _ => throw new NotSupportedException(method.Name)
            };
        }

        private Task<IResponse?> GotoAsync(string url)
        {
            Url = url;
            var response = _responseFactory(new Uri(url))
                ?? throw new InvalidOperationException($"No fixture registered for {url}.");

            _root = XDocument.Parse($"<root>{response.Body}</root>", LoadOptions.PreserveWhitespace).Root!;
            return Task.FromResult<IResponse?>(null);
        }

        private ILocator CreateLocator(IReadOnlyList<XElement> elements)
        {
            return DynamicProxy<ILocator>.Create((method, args) => InvokeLocator(method, args, elements));
        }

        private object? InvokeLocator(MethodInfo method, object?[]? args, IReadOnlyList<XElement> elements)
        {
            return method.Name switch
            {
                nameof(ILocator.CountAsync) => Task.FromResult(elements.Count),
                nameof(ILocator.Nth) => CreateLocator(elements.Skip((int)args![0]!).Take(1).ToList()),
                "get_First" => CreateLocator(elements.Take(1).ToList()),
                "get_Last" => CreateLocator(elements.TakeLast(1).ToList()),
                nameof(ILocator.InnerHTMLAsync) => Task.FromResult(GetInnerHtml(elements.FirstOrDefault())),
                nameof(ILocator.InnerTextAsync) => Task.FromResult(GetInnerText(elements.FirstOrDefault())),
                nameof(ILocator.WaitForAsync) => WaitForAsync(elements),
                nameof(ILocator.Locator) => CreateLocator(elements.SelectMany(element => Select(element, (string)args![0]!)).ToList()),
                nameof(ILocator.IsVisibleAsync) => Task.FromResult(elements.Count > 0),
                nameof(ILocator.IsEnabledAsync) => Task.FromResult(elements.Count > 0 && elements[0].Attribute("disabled") is null),
                nameof(ILocator.GetAttributeAsync) => Task.FromResult(elements.FirstOrDefault()?.Attribute((string)args![0]!)?.Value),
                nameof(ILocator.ScrollIntoViewIfNeededAsync) => Task.CompletedTask,
                nameof(ILocator.ClickAsync) => Task.CompletedTask,
                "get_Page" => _page ?? throw new InvalidOperationException("Page has not been attached."),
                _ => throw new NotSupportedException(method.Name)
            };
        }

        private static Task WaitForAsync(IReadOnlyList<XElement> elements)
        {
            if (elements.Count == 0)
            {
                throw new TimeoutException("Element not found.");
            }

            return Task.CompletedTask;
        }

        private static string GetInnerHtml(XElement? element)
        {
            return element is null
                ? string.Empty
                : string.Concat(element.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
        }

        private static string GetInnerText(XElement? element)
        {
            return element is null
                ? string.Empty
                : string.Concat(element.DescendantNodesAndSelf().OfType<XText>().Select(text => text.Value)).Trim();
        }

        private static IReadOnlyList<XElement> Select(XElement root, string selector)
        {
            var matches = new List<XElement>();

            foreach (var branch in Split(selector, ','))
            {
                var steps = ParseSteps(branch);
                IEnumerable<XElement> current = [root];

                foreach (var step in steps)
                {
                    current = step.Combinator switch
                    {
                        '>' => current.SelectMany(element => element.Elements().Where(candidate => Matches(candidate, step.Selector))),
                        _ => current.SelectMany(element => element.Descendants().Where(candidate => Matches(candidate, step.Selector)))
                    };
                }

                matches.AddRange(current);
            }

            return matches
                .Distinct(new XElementIdentityComparer())
                .ToList();
        }

        private static IReadOnlyList<(char Combinator, string Selector)> ParseSteps(string selector)
        {
            var steps = new List<(char, string)>();
            var buffer = new System.Text.StringBuilder();
            var combinator = ' ';
            var inAttribute = false;

            foreach (var character in selector.Trim())
            {
                if (character == '[')
                {
                    inAttribute = true;
                }
                else if (character == ']')
                {
                    inAttribute = false;
                }

                if (!inAttribute && (character == '>' || character == ' '))
                {
                    if (buffer.Length > 0)
                    {
                        steps.Add((combinator, buffer.ToString().Trim()));
                        buffer.Clear();
                    }

                    combinator = character;
                    continue;
                }

                buffer.Append(character);
            }

            if (buffer.Length > 0)
            {
                steps.Add((combinator, buffer.ToString().Trim()));
            }

            return steps;
        }

        private static IEnumerable<string> Split(string value, char separator)
        {
            var result = new List<string>();
            var buffer = new System.Text.StringBuilder();
            var depth = 0;

            foreach (var character in value)
            {
                if (character == '[')
                {
                    depth++;
                }
                else if (character == ']')
                {
                    depth--;
                }

                if (depth == 0 && character == separator)
                {
                    result.Add(buffer.ToString().Trim());
                    buffer.Clear();
                    continue;
                }

                buffer.Append(character);
            }

            if (buffer.Length > 0)
            {
                result.Add(buffer.ToString().Trim());
            }

            return result.Where(item => !string.IsNullOrWhiteSpace(item));
        }

        private static bool Matches(XElement element, string selector)
        {
            var tagMatch = System.Text.RegularExpressions.Regex.Match(selector, @"^[a-zA-Z][a-zA-Z0-9-]*");
            if (tagMatch.Success && !string.Equals(element.Name.LocalName, tagMatch.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (System.Text.RegularExpressions.Match classMatch in System.Text.RegularExpressions.Regex.Matches(selector, @"\.([a-zA-Z0-9_-]+)"))
            {
                var classValue = element.Attribute("class")?.Value ?? string.Empty;
                var classes = classValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!classes.Contains(classMatch.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var idMatch = System.Text.RegularExpressions.Regex.Match(selector, @"#([a-zA-Z0-9_-]+)");
            if (idMatch.Success && !string.Equals(element.Attribute("id")?.Value, idMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (System.Text.RegularExpressions.Match attributeMatch in System.Text.RegularExpressions.Regex.Matches(selector, @"\[(?<name>[^\^\*\]=]+)(?<op>\^=|\*=|=)?'?(?<value>[^\]']*)'?\]"))
            {
                var name = attributeMatch.Groups["name"].Value.Trim().Trim('"');
                var actual = element.Attribute(name)?.Value;
                var op = attributeMatch.Groups["op"].Value;
                var expected = attributeMatch.Groups["value"].Value.Trim().Trim('"');

                if (string.IsNullOrWhiteSpace(op))
                {
                    if (actual is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (actual is null)
                {
                    return false;
                }

                var matchesAttribute = op switch
                {
                    "=" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                    "*=" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
                    "^=" => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };

                if (!matchesAttribute)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class XElementIdentityComparer : IEqualityComparer<XElement>
    {
        public bool Equals(XElement? x, XElement? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(XElement obj)
        {
            return obj.GetHashCode();
        }
    }

    private class DynamicProxy<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = default!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod!, args);
        }

        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = DispatchProxy.Create<T, DynamicProxy<T>>();
            ((DynamicProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}
