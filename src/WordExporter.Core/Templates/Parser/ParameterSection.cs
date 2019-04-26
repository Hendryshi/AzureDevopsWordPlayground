﻿using Sprache;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WordExporter.Core.WordManipulation;

namespace WordExporter.Core.Templates.Parser
{
    public sealed class ParameterSection : Section
    {
        private ParameterSection()
        {
        }

        public List<String> ParameterNames { get; private set; }

        #region syntax

        private readonly static Parser<string> Line = Parse
            .AnyChar
            .Except(Parse.LineEnd)
            .AtLeastOnce()
            .Token()
            .Text();

        public readonly static Parser<ParameterSection> Parser =
            from sections in Line.Many()
            select new ParameterSection()
            {
                ParameterNames = sections.ToList()
            };

        #endregion
    }
}
