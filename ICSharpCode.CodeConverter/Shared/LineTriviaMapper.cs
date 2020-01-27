﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.CodeConverter.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ICSharpCode.CodeConverter.Shared
{
    internal class LineTriviaMapper
    {
        private readonly SyntaxNode source;
        private readonly TextLineCollection sourceLines;
        private readonly TextLineCollection originalTargetLines;
        private readonly IReadOnlyDictionary<int, TextLine> targetLeadingTextLineFromSourceLine;
        private readonly IReadOnlyDictionary<int, TextLine> targetTrailingTextLineFromSourceLine;

        public LineTriviaMapper(SyntaxNode source, TextLineCollection sourceLines, TextLineCollection originalTargetLines, Dictionary<int, TextLine> targetLeadingTextLineFromSourceLine, Dictionary<int, TextLine> targetTrailingTextLineFromSourceLine)
        {
            this.source = source;
            this.sourceLines = sourceLines;
            this.originalTargetLines = originalTargetLines;
            this.targetLeadingTextLineFromSourceLine = targetLeadingTextLineFromSourceLine;
            this.targetTrailingTextLineFromSourceLine = targetTrailingTextLineFromSourceLine;
        }

        /// <summary>
        /// For each source line:
        /// * Add leading trivia to the start of the first target line containing a node converted from that source line
        /// * Add trailing trivia to the end of the last target line containing a node converted from that source line
        /// Makes no attempt to convert whitespace/newline-only trivia
        /// Currently doesn't deal with any within-line trivia (i.e. /* block comments */)
        /// </summary>
        public static SyntaxNode MapSourceTriviaToTarget(SyntaxNode source, SyntaxNode target)
        {
            var originalTargetLines = target.GetText().Lines;

            var targetNodesBySourceStartLine = target.GetAnnotatedNodesAndTokens(AnnotationConstants.SourceStartLineAnnotationKind)
                .ToLookup(n => n.GetAnnotations(AnnotationConstants.SourceStartLineAnnotationKind).Select(a => int.Parse(a.Data)).Min())
                .ToDictionary(g => g.Key, g => originalTargetLines.GetLineFromPosition(g.Min(x => x.GetLocation().SourceSpan.Start)));

            var targetNodesBySourceEndLine = target.GetAnnotatedNodesAndTokens(AnnotationConstants.SourceEndLineAnnotationKind)
                .ToLookup(n => n.GetAnnotations(AnnotationConstants.SourceEndLineAnnotationKind).Select(a => int.Parse(a.Data)).Max())
                .ToDictionary(g => g.Key, g => originalTargetLines.GetLineFromPosition(g.Max(x => x.GetLocation().SourceSpan.End)));

            var sourceLines = source.GetText().Lines;
            var lineTriviaMapper = new LineTriviaMapper(source, sourceLines, originalTargetLines, targetNodesBySourceStartLine, targetNodesBySourceEndLine);
            return lineTriviaMapper.GetTargetWithSourceTrivia(target);
        }

        private SyntaxNode GetTargetWithSourceTrivia(SyntaxNode target)
        {
            //TODO Try harder to avoid losing track of various precalculated positions changing during the replacements, for example build up a dictionary of replacements and make them in a single ReplaceTokens call
            //TODO Keep track of lost comments and put them in a comment at the end of the file
            for (int i = sourceLines.Count - 1; i >= 0; i--) {
                var sourceLine = sourceLines[i];
                var endOfSourceLine = source.FindToken(sourceLine.End);
                var startOfSourceLine = source.FindTokenOnRightOfPosition(sourceLine.Start);

                if (endOfSourceLine.TrailingTrivia.Any(t => !t.IsWhitespaceOrEndOfLine())) {
                    var line = GetBestLine(targetTrailingTextLineFromSourceLine, i);
                    if (line != default) {
                        var convertedTrivia = endOfSourceLine.TrailingTrivia.ConvertTrivia();
                        var toReplace = target.FindToken(line.End);
                        target = target.ReplaceToken(toReplace, toReplace.WithTrailingTrivia(convertedTrivia));
                    }
                }

                if (startOfSourceLine.LeadingTrivia.Any(t => !t.IsWhitespaceOrEndOfLine())) {
                    var line = GetBestLine(targetLeadingTextLineFromSourceLine, i);
                    if (line != default) {
                        var convertedTrivia = startOfSourceLine.LeadingTrivia.ConvertTrivia();
                        var toReplace = target.FindTokenOnRightOfPosition(line.Start);
                        target = target.ReplaceToken(toReplace, toReplace.WithLeadingTrivia(convertedTrivia));
                    }
                }
            }
            return target;
        }

        private TextLine GetBestLine(IReadOnlyDictionary<int, TextLine> sourceToTargetLine, int sourceLineIndex)
        {
            if (sourceToTargetLine.TryGetValue(sourceLineIndex, out var targetLineIndex)) return targetLineIndex;

            var (previousOffset, previousLine) = GetOffsetSourceLineTargetNodes(sourceToTargetLine, sourceLineIndex, -1);
            var (nextOffset, nextLine) = GetOffsetSourceLineTargetNodes(sourceToTargetLine, sourceLineIndex, 1);
            if (previousLine != default && nextLine != default) {
                var guessedTargetLine = originalTargetLines[((previousLine.LineNumber - previousOffset) + (nextLine.LineNumber - nextOffset)) / 2];
                //TODO Annotate this case with a comment to say it's a guess
                //TODO Move this guessing phase to fill in the gaps after all other allocations are made to avoid clashes with other moved lines
                if (previousLine.LineNumber < guessedTargetLine.LineNumber && guessedTargetLine.LineNumber < nextLine.LineNumber) return guessedTargetLine;
            }
            return default;
        }

        private static (int offset, TextLine targetLine) GetOffsetSourceLineTargetNodes(IReadOnlyDictionary<int, TextLine> sourceToTargetLine, int sourceLineIndex, int multiplier)
        {
            for (int offset = 1; offset <= 5; offset++) {
                if (sourceToTargetLine.TryGetValue(sourceLineIndex + (offset * multiplier), out var line)) {
                    return (offset * multiplier, line);
                }
            }
            return (0, default);
        }
    }
}