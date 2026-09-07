using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// M6 识别层测试。
/// <para><strong>夹具不是编的</strong>：那 8 行 OCR 文本是 Windows 内置识别引擎对一张自渲染唛头图的真实输出
/// （由 <c>labelgou-other\_probe\ocr-tfm\</c> 跑出、直接以 UTF-8 落盘取回，没经过会污染字符的管道），
/// 模型那份 JSON 是 Ollama <c>qwen3-vl:4b</c> 对同一张图的真实返回。
/// 用真实脏样本做夹具才有价值——它当场就抓出了「重量被静默读成 5 KGS」这个会印错的缺陷。</para>
/// </summary>
public class RecognitionTests
{
    /// <summary>真实 OCR 输出（原样照抄，含它犯的每一个错）。</summary>
    private static readonly string[] RealOcrLines =
    {
        "SHANGHAI · > LOS ANGELES, U SA",
        "C/S: MACYS （ 0 NTRACT NO: MM 2603",
        "PO NO: 2024 ． 0817 ITEM NO: A ． 778",
        "G.W.: 25 ． 5 KGS N .W.: 22 · 1 KGS",
        "M EAS: 60x40x30 CM CBM: 0 ． 072",
        "MADE IN CHINA",
        "No. 3 / 12",
        "上 海 到 洛 杉 矶 目 的 港",
    };

    /// <summary>真实模型返回（第一次运行；JSON 落在 thinking 字段里）。</summary>
    private const string RealLlmJson = """
    {
      "consignee": null,
      "clientCode": "MACYS",
      "contractNo": "MMJ-2603",
      "poNumber": "2024-0817",
      "itemNo": "A-778",
      "destinationPort": "LOS ANGELES, USA",
      "cartonNo": "No. 3 / 12",
      "cartonTotal": "12",
      "grossWeight": "25.5 KGS",
      "netWeight": "22.1 KGS",
      "measurement": "60x40x30 CM",
      "boxSize": "0.072 CBM",
      "origin": "MADE IN CHINA",
      "remarks": "上海到洛杉矶 目的港"
    }
    """;

    private static RecognizedText OcrFixture()
        => RecognizedText.FromLines(TextChannel.Ocr, "probe-label.png", RealOcrLines);

    private static List<FieldCandidate> TextCandidates() => RuleFieldExtractor.Extract(OcrFixture()).Candidates;

    private static List<FieldCandidate> LlmCandidates() => LlmFieldJsonParser.Parse(RealLlmJson).Candidates;

    private static IReadOnlyList<ReviewedField> Merged()
        => CrossValidator.Merge(OcrFixture(), TextCandidates(), LlmCandidates());

    private static ReviewedField One(IReadOnlyList<ReviewedField> all, MarkFieldKey key)
        => Assert.Single(all, f => f.Field == key);

    // ---------- 归一化：先证明我们看懂了真实 OCR 会犯哪些错 ----------

    [Fact]
    public void RepairDecimalSeparatorsHandlesTheRealSpacedShapes()
    {
        var repaired = TextNormalizer.RepairDecimalSeparators(RealOcrLines[3]);

        Assert.Contains("25.5 KGS", repaired, StringComparison.Ordinal);
        Assert.Contains("22.1 KGS", repaired, StringComparison.Ordinal);

        // 尺寸里的 x 不能被牵连：三段数字该保持三段，粘起来就成了 604030 这种怪物。
        Assert.Contains("60x40x30", TextNormalizer.RepairDecimalSeparators(RealOcrLines[4]), StringComparison.Ordinal);
    }

    [Fact]
    public void CompactGluesOcrWordSplitsBackTogether()
    {
        Assert.Equal(TextNormalizer.Compact("N.W."), TextNormalizer.Compact("N .W."));
        Assert.Equal("nw", TextNormalizer.Compact("N .W.:"));

        // 中文被逐字加空格是本机 OCR 的真实行为，压缩形必须把它粘回去，否则证据永远匹配不上。
        Assert.Equal(
            TextNormalizer.Compact("上海到洛杉矶 目的港"),
            TextNormalizer.Compact(RealOcrLines[7]));
    }

    [Fact]
    public void HalfwidthClearsTheFullwidthPunctuationOcrProduces()
    {
        var half = TextNormalizer.ToHalfwidth(RealOcrLines[1]);

        Assert.DoesNotContain('（', half);
        Assert.DoesNotContain('．', TextNormalizer.ToHalfwidth(RealOcrLines[2]));
    }

    [Fact]
    public void DigitsCoveredIgnoresDecimalPlacementButNotDigitCount()
    {
        Assert.True(TextNormalizer.DigitsCovered("25.5", "25 5"));
        Assert.False(TextNormalizer.DigitsCovered("25.5", "22.1"));
        Assert.True(TextNormalizer.DigitsCovered("LOS ANGELES", "随便什么"));   // 没数字的值不由这条管
    }

    // ---------- 字段规范化：会印错的那一类 ----------

    [Fact]
    public void WeightNeverSilentlyLosesTheIntegerPart()
    {
        // 真实回归：OCR 给的是 "25 ． 5 KGS"。若不先修小数点，重量正则会从 "5 KGS" 处匹配成功，
        // 于是 25.5 被静默印成 5 —— 一个编译、运行、单测全都不会报的印刷错误。
        var weight = FieldNormalizer.Normalize(MarkFieldKey.GrossWeight, "25 ． 5 KGS");

        Assert.Equal("25.5 KGS", weight.Value);
        Assert.Null(weight.Warning);
    }

    [Theory]
    [InlineData("25.5", "单位")]                    // 缺单位：不能替用户猜公斤还是磅
    [InlineData("0 KGS", "0 或负数")]
    [InlineData("-3 KGS", "0 或负数")]
    [InlineData("99999 KGS", "超出合理范围")]
    public void WeightProblemsAreSpokenOutLoud(string raw, string expectedInWarning)
    {
        var weight = FieldNormalizer.Normalize(MarkFieldKey.GrossWeight, raw);

        Assert.NotNull(weight.Warning);
        Assert.Contains(expectedInWarning, weight.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxSizeAndVolumeAreNotInterchangeable()
    {
        var size = FieldNormalizer.Normalize(MarkFieldKey.BoxSize, "60x40x30 CM");
        Assert.Equal("60×40×30 CM", size.Value);
        Assert.Null(size.Warning);

        // 把 CBM 填进尺寸、或把尺寸填进体积，正是模型这次真实犯错的形状。
        var volumeHoldingSize = FieldNormalizer.Normalize(MarkFieldKey.Measurement, "60x40x30 CM");
        Assert.Contains("外箱尺寸", volumeHoldingSize.Warning, StringComparison.Ordinal);

        var sizeHoldingVolume = FieldNormalizer.Normalize(MarkFieldKey.BoxSize, "0.072 CBM");
        Assert.Contains("没认出", sizeHoldingVolume.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CartonPairSplitsIntoThisCartonAndTotal()
    {
        Assert.Equal("3", FieldNormalizer.Normalize(MarkFieldKey.CartonNo, "No. 3 / 12").Value);
        Assert.Equal("12", FieldNormalizer.Normalize(MarkFieldKey.CartonTotal, "No. 3 / 12").Value);

        // 一个格子里冒出多个数字时取第一个，但必须留话，不能静默选。
        var messy = FieldNormalizer.Normalize(MarkFieldKey.Quantity, "12 24");
        Assert.Equal("12", messy.Value);
        Assert.NotNull(messy.Warning);
    }

    [Fact]
    public void AmbiguousSlashDateIsNotSilentlyGuessed()
    {
        var bothLegal = FieldNormalizer.Normalize(MarkFieldKey.ShipDate, "3/4/2026");
        Assert.Equal("2026-03-04", bothLegal.Value);              // 按美式 M/d 读
        Assert.Contains("请核对", bothLegal.Warning, StringComparison.Ordinal);

        var onlyOneWay = FieldNormalizer.Normalize(MarkFieldKey.ShipDate, "17/09/2026");
        Assert.Equal("2026-09-17", onlyOneWay.Value);
        Assert.Null(onlyOneWay.Warning);                          // 17 不可能是月份，读得确定就不该骚扰用户

        Assert.Equal("2026-09-07", FieldNormalizer.Normalize(MarkFieldKey.ShipDate, "2026年9月7日").Value);
        Assert.Contains("请核对", FieldNormalizer.Normalize(MarkFieldKey.ShipDate, "下周三").Warning, StringComparison.Ordinal);
    }

    // ---------- 规则抽取：能不能从真实脏文本里锚出字段 ----------

    [Fact]
    public void RuleExtractorAnchorsLabelsDespiteOcrNoise()
    {
        var found = TextCandidates().ToDictionary(c => c.Field, c => c);

        Assert.Equal("25.5 KGS", FieldNormalizer.Normalize(MarkFieldKey.GrossWeight, found[MarkFieldKey.GrossWeight].RawValue).Value);
        Assert.True(found.ContainsKey(MarkFieldKey.NetWeight), "N .W. 这种带空格的标签必须还能锚住");
        Assert.True(found.ContainsKey(MarkFieldKey.ItemNo), "ITEM NO 行");
        Assert.Equal("3", FieldNormalizer.Normalize(MarkFieldKey.CartonNo, found[MarkFieldKey.CartonNo].RawValue).Value);
        Assert.Equal("12", FieldNormalizer.Normalize(MarkFieldKey.CartonTotal, found[MarkFieldKey.CartonTotal].RawValue).Value);
        Assert.Contains("CHINA", found[MarkFieldKey.Origin].RawValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BareNoLabelNeverEatsAnAdjacentAlphanumericCode()
    {
        // 真实误锚："（ 0 NTRACT NO: MM 2603" 里那个裸 "NO:" 不是件号，
        // 它是被 OCR 打残的合同号标签。件号位上绝不能收下带字母的值。
        var text = RecognizedText.FromLines(TextChannel.Ocr, "one-line.png", new[]
        {
            "C/S: MACYS （ 0 NTRACT NO: MM 2603",
        });

        var found = RuleFieldExtractor.Extract(text).Candidates;
        Assert.DoesNotContain(found, c => c.Field == MarkFieldKey.CartonNo);
    }

    [Fact]
    public void RuleExtractorDoesNotInventWhatItCannotSee()
    {
        // CONTRACT 被 OCR 读成「（ 0 NTRACT」，C 变成了 0 —— 这条标签注定不可能锚中。
        // 单通道锚不到是事实，写死成断言，防止以后有人为了"过测试"去加一条会误伤的正则。
        var found = TextCandidates().Select(c => c.Field).ToList();

        Assert.DoesNotContain(MarkFieldKey.ContractNo, found);
        Assert.DoesNotContain(MarkFieldKey.DestinationPort, found);   // 图上根本没有 POD/destination 字样
    }

    [Fact]
    public void EveryRuleCandidateCarriesItsEvidenceLine()
    {
        foreach (var candidate in TextCandidates())
        {
            Assert.True(candidate.EvidenceLineIndex >= 0, $"{candidate.Field} 没有证据行");

            // 证据就是那一行的原文（不是改写后的），用户点“看原图”时要能一一对上。
            Assert.Equal(RealOcrLines[candidate.EvidenceLineIndex], candidate.Evidence);
        }
    }

    [Fact]
    public void ShortAliasesDoNotMatchInMidWord()
    {
        // "of" 是「总件数」的别名之一；一句普通英文里到处是 of，绝不能因此造出一个总件数。
        var noise = RecognizedText.FromLines(TextChannel.Ocr, "noise.png", new[]
        {
            "Certificate of Origin issued by the council of ministers",
        });

        var found = RuleFieldExtractor.Extract(noise).Candidates;
        Assert.DoesNotContain(found, c => c.Field == MarkFieldKey.CartonTotal);
    }

    // ---------- 模型响应解析 ----------

    [Fact]
    public void ThinkingFieldIsUsedWhenResponseIsEmpty()
    {
        // 本机真实形状：Ollama 把 JSON 放在 thinking，response 是空串。只读 response 会误判成"模型没返回"。
        Assert.Equal(RealLlmJson, LlmFieldJsonParser.PickBestJson(string.Empty, RealLlmJson));
        Assert.Equal(RealLlmJson, LlmFieldJsonParser.PickBestJson(RealLlmJson, "一堆废话"));
        Assert.Null(LlmFieldJsonParser.PickBestJson(null, null));
    }

    [Fact]
    public void RealModelJsonParsesIntoCandidates()
    {
        var result = LlmFieldJsonParser.Parse("以下是识别结果：\n```json\n" + RealLlmJson + "\n```");

        Assert.Equal(0, result.DroppedKeys);
        Assert.True(result.Candidates.Count >= 10, $"真实返回应有十来个字段，只解析出 {result.Candidates.Count} 个");

        var port = Assert.Single(result.Candidates, c => c.Field == MarkFieldKey.DestinationPort);
        Assert.Equal("LOS ANGELES, USA", port.RawValue);
        Assert.Equal(ValueOrigin.AiLlm, port.Origin);

        // consignee 是 null：图上确实没有收货人行，"没有"是有效回答，不该变成候选值也不该报警。
        Assert.DoesNotContain(result.Candidates, c => c.Field == MarkFieldKey.Consignee);
    }

    [Fact]
    public void KeysOutsideTheFieldWhitelistAreDroppedWithAWarning()
    {
        var result = LlmFieldJsonParser.Parse("""{ "GrossWeight": "10 KGS", "TotalWeight": 12, "coordinates": [1, 2] }""");

        Assert.Equal(2, result.DroppedKeys);   // TotalWeight 与 coordinates 都不在唛头字段清单里
        Assert.Contains(result.Warnings, w => w.Contains("白名单", StringComparison.Ordinal));
        Assert.Single(result.Candidates);   // 只有合法键留下
    }

    [Fact]
    public void GarbageResponseWarnsInsteadOfThrowing()
    {
        Assert.Contains(LlmFieldJsonParser.Parse(null).Warnings, w => w.Contains("没有返回内容", StringComparison.Ordinal));
        Assert.Contains(LlmFieldJsonParser.Parse("我不知道").Warnings, w => w.Contains("找不到 JSON 对象", StringComparison.Ordinal));
        Assert.Contains(LlmFieldJsonParser.Parse("{ 坏掉的").Warnings, w => w.Contains("找不到 JSON 对象", StringComparison.Ordinal));
        Assert.Contains(LlmFieldJsonParser.Parse("{ \"GrossWeight\": }").Warnings, w => w.Contains("格式不对", StringComparison.Ordinal));
    }

    // ---------- 交叉校验：D12/D13 的实际行为 ----------

    [Fact]
    public void AgreeingChannelsLiftConfidenceButStillNeedReview()
    {
        var gross = One(Merged(), MarkFieldKey.GrossWeight);

        Assert.True(gross.Agreed, "两通道都读到 25.5 KGS，应判一致");
        Assert.Equal("25.5 KGS", gross.Value);
        // 不写 >= 0.8：置信度是 0.7 + 0.1 算出来的，double 下等于 0.7999999999999999，会假红。
        // 断言只表达意图：一致时要明高于单通道，但不许超过 0.95（没有机器可以接近确定）。
        Assert.True(gross.Confidence >= 0.75 && gross.Confidence <= 0.95,
            $"一致时的置信度不合理：{gross.Confidence:F4}");
        Assert.True(gross.IsPending, "D13：一致也要人确认，AI 来的字段不存在自动通过");
    }

    [Fact]
    public void ModelSemanticSwapIsCaughtAsDisagreement()
    {
        // 真实犯错：模型把 0.072 CBM 放进了 boxSize，而文本层的尺寸是 60x40x30 CM。
        var box = One(Merged(), MarkFieldKey.BoxSize);

        Assert.False(box.Agreed);
        Assert.Contains("两通道不一致", box.Warning, StringComparison.Ordinal);
        Assert.Contains("0.072", box.LlmValue, StringComparison.Ordinal);
        Assert.True(box.IsHot);
    }

    [Fact]
    public void ModelOnlyValueWithoutEvidenceIsFlaggedAsPossibleFabrication()
    {
        // 图上没有目的港字样（真实情形），模型却给一个。OCR 文本层找不到它 → 必须压到最低档并明说。
        var text = RecognizedText.FromLines(TextChannel.Ocr, "no-port.png", new[] { "G.W.: 25 ． 5 KGS" });
        var llm = LlmFieldJsonParser.Parse("""{ "DestinationPort": "ROTTERDAM, NL" }""").Candidates;

        var merged = CrossValidator.Merge(text, RuleFieldExtractor.Extract(text).Candidates, llm);
        var port = One(merged, MarkFieldKey.DestinationPort);

        Assert.True(port.Confidence < 0.4, $"无证据的值不该有 {port.Confidence:F2} 的置信度");
        Assert.Equal(EvidenceLevel.None, port.Evidence);
        Assert.Contains("证据", port.Warning ?? string.Empty, StringComparison.Ordinal);
        Assert.True(port.IsHot);
    }

    [Fact]
    public void ModelOnlyValueFoundInTextLayerGetsMiddleConfidence()
    {
        // 规则通道锚不到标签（图上没写 POD），但模型给的值确实来自图上的文字 → 文本层找得到，中置信。
        var text = OcrFixture();
        var llm = LlmFieldJsonParser.Parse("""{ "DestinationPort": "LOS ANGELES, USA" }""").Candidates;

        var port = One(CrossValidator.Merge(text, RuleFieldExtractor.Extract(text).Candidates, llm), MarkFieldKey.DestinationPort);

        Assert.True(port.Confidence >= 0.6, $"文本层里能找到原文，置信度不该低于 0.6，实为 {port.Confidence:F2}");
        Assert.True(port.Evidence >= EvidenceLevel.Compact);
        Assert.DoesNotContain("编", port.Warning ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void CjkValueWithLetterSpacingStillMatchesItsEvidence()
    {
        // 本机 OCR 把中文逐字加空格，而模型给的是连着写的同一句话：
        // 证据匹配必须在压缩形上成立，否则所有中文备注都会被当成"模型编的"误报。
        var evidence = new EvidencePool(OcrFixture()).Find("上海到洛杉矶 目的港");

        Assert.True(evidence.Level >= EvidenceLevel.Compact, $"只读到 {evidence.Level}：{evidence.Line}");
        Assert.Equal(7, evidence.LineIndex);
    }

    [Fact]
    public void ShortValuesNeedAStandaloneHitToCountAsEvidence()
    {
        var pool = new EvidencePool(OcrFixture());

        // 真样本跑出来的：件号 "3" 在 "MM 2603" 里也算“原文命中”，于是给用户指一条毫不相干的证据行。
        // 一两个字符的值必须整词命中才算证据，否则核对着错的原文看，比不给证据还坏。
        var three = pool.Find("3");
        Assert.Equal(EvidenceLevel.Exact, three.Level);
        Assert.Contains("No. 3 / 12", three.Line, StringComparison.Ordinal);

        Assert.Contains("No. 3 / 12", pool.Find("12").Line, StringComparison.Ordinal);

        // 长值不受这道闸影响
        Assert.Equal(EvidenceLevel.Exact, pool.Find("MADE IN CHINA").Level);
    }

    [Fact]
    public void GrossLighterThanNetIsFlaggedAsPhysicallyImpossible()
    {
        var text = RecognizedText.FromLines(TextChannel.Ocr, "bad.png", new[] { "G.W.: 10 KGS N.W.: 20 KGS" });

        var merged = CrossValidator.Merge(text, RuleFieldExtractor.Extract(text).Candidates, null);
        var gross = One(merged, MarkFieldKey.GrossWeight);

        Assert.Contains("不可能", gross.Warning, StringComparison.Ordinal);
        Assert.False(gross.Agreed);
    }

    // ---------- 批次与落库接缝 ----------

    [Fact]
    public void BulkConfirmOnlyPassesRowsTheMachineIsReallySureAbout()
    {
        var batch = new RecognizedBatch { SourceName = "probe-label.png" };
        foreach (var field in Merged()) batch.Fields.Add(field);

        var confirmed = batch.BulkConfirm();

        Assert.True(confirmed > 0, "一致且有原文证据的行应能批量确认，实际一个都没过");
        Assert.True(batch.PendingCount > 0, "不一致/无证据的行必须还留着，不许被批量确认带走");

        foreach (var pending in batch.Fields.Where(f => f.IsPending))
        {
            Assert.True(!pending.Agreed || pending.Warning is not null || pending.Evidence == EvidenceLevel.None
                        || string.IsNullOrWhiteSpace(pending.TextValue) || string.IsNullOrWhiteSpace(pending.LlmValue),
                $"{pending.ChineseName} 被留在待核，但它两路都有值、一致又无告警，说明批量确认条件太严");
        }
    }

    [Fact]
    public void SingleChannelRowsNeverRideTheBulkConfirm()
    {
        // 真样本抓出来的坑：规则通道把客户代码读成 "MACYS ( 0 NTRACT"（CONTRACT 被 OCR 掉了字，锚点断不到下一个标签上）。
        // 它在原文里确实整段存在 → 证据是 Exact、“一致”也成立（只有一路说话时 Agreed 永远是真），
        // 旧条件会把它自动放行 —— 而这正是会被印上去的错值。
        var batch = new RecognizedBatch { SourceName = "probe-label.png" };
        batch.Fields.Add(new ReviewedField
        {
            Field = MarkFieldKey.ClientCode,
            Value = "MACYS ( 0 NTRACT",
            TextValue = "MACYS ( 0 NTRACT",      // 只有文本层一路开了口
            Origin = ValueOrigin.AiOcr,
            Confidence = 0.58,
            Evidence = EvidenceLevel.Exact,
            Agreed = true,
        });

        Assert.Equal(0, batch.BulkConfirm());
        Assert.True(batch.Fields[0].IsPending, "只有一路给的值必须人看一眼");
    }

    [Fact]
    public void EditingAValueKnocksItBackToPending()
    {
        var batch = new RecognizedBatch { SourceName = "x.png" };
        batch.Fields.Add(new ReviewedField
        {
            Field = MarkFieldKey.ContractNo,
            Value = "MM 2603",
            TextValue = "MM 2603",
            LlmValue = "MM 2603",
            Origin = ValueOrigin.AiOcr,
            Confidence = 0.9,
            Evidence = EvidenceLevel.Exact,
            EvidenceText = "C/S: MACYS （ 0 NTRACT NO: MM 2603",
        });

        Assert.Equal(1, batch.BulkConfirm());

        batch.Fields[0].Value = "MMJ-2603";

        Assert.True(batch.Fields[0].IsPending, "改过的值等于人重新引入了不确定性，必须再确认一次");
        Assert.Equal(1, batch.Fields[0].EditCount);

        batch.Fields[0].Confirmed = true;
        Assert.False(batch.Fields[0].IsPending);
        Assert.True(batch.ReadyToImport);
    }

    [Fact]
    public void UnconfirmedFieldsReachTheRecordWithTheReviewFlagSoThePrintGateStopsThem()
    {
        var batch = new RecognizedBatch { SourceName = "probe-label.png" };
        foreach (var field in Merged()) batch.Fields.Add(field);

        var record = batch.ToRecord(1);

        // 不是"没确认就不写"，而是"写了但带 NeedsReview"——预览要标红、闸门要拦，用户得看得见自己还欠核对。
        Assert.NotEmpty(record.PendingReview());
        var gross = record.Get(MarkFieldKey.GrossWeight);
        Assert.NotNull(gross);
        Assert.True(gross!.NeedsReview);
        Assert.True(gross!.IsAiSourced);
        Assert.Contains("probe-label.png", gross!.SourceRef, StringComparison.Ordinal);
        Assert.Contains("行", gross!.SourceRef, StringComparison.Ordinal);   // 可回溯到具体哪一行

        batch.Fields.ForEach(f => f.Confirmed = true);
        var confirmed = batch.ToRecord(1);
        Assert.Empty(confirmed.PendingReview());
    }

    [Fact]
    public void EmptyValuesAreNotWrittenAsPendingFakeFields()
    {
        var batch = new RecognizedBatch { SourceName = "x.png" };
        batch.Fields.Add(new ReviewedField { Field = MarkFieldKey.BatchNo, Value = "  ", Origin = ValueOrigin.AiLlm });

        var record = batch.ToRecord(1);

        Assert.False(record.Has(MarkFieldKey.BatchNo));
        Assert.Equal(0, batch.PendingCount);   // 本来就没这东西，不该算成"待核"欠账
    }
}
