namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic eval suite (#66): two stages (retrieval, generation), a
/// committed labeled set with a human-labeled sample, a conformant recorded
/// run, and a committed baseline. Variants seed the failure modes (regressions,
/// dropped cases, unknown cases, stray metrics, judge miscalibration) so the
/// tests can prove the gate bites and never passes silently.
/// </summary>
public sealed class EvalAppFixture : IDisposable
{
    public EvalAppFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-eval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ConfigPath = Path.Combine(Root, "eval.json");

        Write("cases.json", """
            {
              "cases": [
                { "id": "r1", "stage": "retrieval", "input": "query: rotate tenant keys", "expected": "docs/rotation.md", "human": "correct" },
                { "id": "r2", "stage": "retrieval", "input": "query: outbox pattern", "expected": "docs/outbox.md", "human": "correct" },
                { "id": "r3", "stage": "retrieval", "input": "query: load contexts", "expected": "docs/plugins.md" },
                { "id": "g1", "stage": "generation", "input": "summarize docs/rotation.md", "expected": "faithful summary", "human": "correct" },
                { "id": "g2", "stage": "generation", "input": "summarize docs/outbox.md", "expected": "faithful summary" },
                { "id": "g3", "stage": "generation", "input": "summarize docs/plugins.md", "expected": "faithful summary", "human": "incorrect" }
              ]
            }
            """);
        Write("results.json", """
            {
              "run": "assistant-v2",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        Write("baseline.json", """
            {
              "generator": "sfg eval 0.11.0",
              "suite": "docs-eval-test",
              "metrics": {
                "recallAt3": { "value": 0.8333, "cases": {
                  "r1": 1.0,
                  "r2": 1.0,
                  "r3": 0.5
                } },
                "faithfulness": { "value": 0.9, "cases": {
                  "g1": 1.0,
                  "g2": 0.9,
                  "g3": 0.8
                } }
              }
            }
            """);
        Write("eval.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [
                { "id": "retrieval", "metrics": ["recallAt3"] },
                { "id": "generation", "metrics": ["faithfulness"] }
              ],
              "thresholds": {
                "recallAt3": 0.8,
                "faithfulness": 0.9
              },
              "cases": "cases.json",
              "results": "results.json",
              "baseline": "baseline.json"
            }
            """);
    }

    public string Root { get; }

    public string ConfigPath { get; }

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
