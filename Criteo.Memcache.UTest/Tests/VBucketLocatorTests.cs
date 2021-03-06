﻿/* Licensed to the Apache Software Foundation (ASF) under one
   or more contributor license agreements.  See the NOTICE file
   distributed with this work for additional information
   regarding copyright ownership.  The ASF licenses this file
   to you under the Apache License, Version 2.0 (the
   "License"); you may not use this file except in compliance
   with the License.  You may obtain a copy of the License at

     http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing,
   software distributed under the License is distributed on an
   "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
   KIND, either express or implied.  See the License for the
   specific language governing permissions and limitations
   under the License.
*/
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Criteo.Memcache.Locator;
using Criteo.Memcache.Node;
using Criteo.Memcache.UTest.Mocks;
using NUnit.Framework;

namespace Criteo.Memcache.UTest.Tests
{
    [TestFixture]
    public class VBucketLocatorTests
    {
        private const uint NodeCount = 3;
        private const uint VBucketCount = 1024;

        private List<IMemcacheNode> _nodes;
        private int[][] _map;
        private VBucketServerMapLocator _locator;

        [TestFixtureSetUp]
        public void SetUp()
        {
            // Create node mocks
            _nodes = new List<IMemcacheNode>();
            for (var i = 0; i < NodeCount; i++)
                _nodes.Add(new NodeMock());

            // One server per bucket, using NodeIndex = VBucketIndex % NodeCount 
            _map = new int[VBucketCount][];
            for (var i = 0; i < VBucketCount; i++)
                _map[i] = new[] { (int)(i % NodeCount) };

            // Initialize locator
            _locator = new VBucketServerMapLocator(_nodes, _map);
        }

        // Value from Couchbase.Tests
        [TestCase("XXXXX", 13701u)]

        // Values generated with Couchbase's implementation
        [TestCase("Sikkim", 99u)]
        [TestCase("wayside", 268u)]
        [TestCase("cabana", 513u)]
        [TestCase("coming", 546u)]
        [TestCase("voyaged", 995u)]
        [TestCase("outcast", 1235u)]
        [TestCase("satisfy", 1521u)]
        [TestCase("blackjacked", 1616u)]
        [TestCase("missal", 1621u)]
        [TestCase("smugger", 2140u)]
        [TestCase("smocking", 2341u)]
        [TestCase("castling", 2365u)]
        [TestCase("confidence", 2423u)]
        [TestCase("Azania", 2557u)]
        [TestCase("Thatcher", 2619u)]
        [TestCase("rotational", 2632u)]
        [TestCase("mussier", 2717u)]
        [TestCase("revived", 3127u)]
        [TestCase("Grünewald", 3331u)]
        [TestCase("incurable", 3410u)]
        [TestCase("retirement", 3464u)]
        [TestCase("abandon", 3467u)]
        [TestCase("pupa", 3531u)]
        [TestCase("replete", 3607u)]
        [TestCase("fickler", 3848u)]
        [TestCase("percolating", 3891u)]
        [TestCase("harry", 3935u)]
        [TestCase("weighing", 4076u)]
        [TestCase("solvable", 4094u)]
        [TestCase("inhered", 4390u)]
        [TestCase("attaching", 4394u)]
        [TestCase("Jeannie", 4395u)]
        [TestCase("Pansy", 4649u)]
        [TestCase("upturn", 4763u)]
        [TestCase("bustled", 4893u)]
        [TestCase("parboil", 4897u)]
        [TestCase("result", 4970u)]
        [TestCase("antitrust", 5000u)]
        [TestCase("electronically", 5201u)]
        [TestCase("moonlighting", 5219u)]
        [TestCase("spud", 5288u)]
        [TestCase("contort", 5413u)]
        [TestCase("fer", 5530u)]
        [TestCase("feverishly", 5921u)]
        [TestCase("exemplary", 5963u)]
        [TestCase("compressed", 5974u)]
        [TestCase("earthshaking", 6271u)]
        [TestCase("Malinda", 6297u)]
        [TestCase("imperialist", 6525u)]
        [TestCase("Nair", 6574u)]
        [TestCase("Goldman", 6919u)]
        [TestCase("coffeehouse", 7136u)]
        [TestCase("cowered", 7354u)]
        [TestCase("frequenting", 7372u)]
        [TestCase("chorusing", 7403u)]
        [TestCase("facial", 7498u)]
        [TestCase("blue", 7734u)]
        [TestCase("boomed", 7811u)]
        [TestCase("signed", 7867u)]
        [TestCase("alderman", 8215u)]
        [TestCase("chancing", 8328u)]
        [TestCase("bloody", 8378u)]
        [TestCase("westerly", 8385u)]
        [TestCase("protracting", 8679u)]
        [TestCase("Mimosa", 8774u)]
        [TestCase("gruel", 8882u)]
        [TestCase("Madeleine", 9167u)]
        [TestCase("worksheet", 9178u)]
        [TestCase("newfangled", 9312u)]
        [TestCase("degrade", 9684u)]
        [TestCase("strain", 9776u)]
        [TestCase("barefaced", 9791u)]
        [TestCase("kilned", 9791u)]
        [TestCase("familiarize", 9932u)]
        [TestCase("spectral", 9972u)]
        [TestCase("democratic", 9974u)]
        [TestCase("documentary", 10234u)]
        [TestCase("fingerprinting", 10360u)]
        [TestCase("bullfighter", 10536u)]
        [TestCase("Chernobyl", 10641u)]
        [TestCase("elsewhere", 10669u)]
        [TestCase("synthesized", 10671u)]
        [TestCase("consecrating", 10795u)]
        [TestCase("gin", 11500u)]
        [TestCase("Langley", 11522u)]
        [TestCase("undercharge", 11526u)]
        [TestCase("fruitlessly", 11685u)]
        [TestCase("disgruntling", 11955u)]
        [TestCase("omitting", 11986u)]
        [TestCase("squalor", 12185u)]
        [TestCase("starred", 12315u)]
        [TestCase("neoprene", 12337u)]
        [TestCase("correctest", 12434u)]
        [TestCase("enervation", 12548u)]
        [TestCase("anchorwomen", 12863u)]
        [TestCase("scorning", 13124u)]
        [TestCase("soaring", 13188u)]
        [TestCase("whippet", 13322u)]
        [TestCase("sledge", 13556u)]
        [TestCase("leafiest", 13690u)]
        [TestCase("overacting", 13802u)]
        [TestCase("impedance", 14111u)]
        [TestCase("gobbed", 14200u)]
        [TestCase("exposure", 14735u)]
        [TestCase("saving", 14812u)]
        [TestCase("Shylock", 14847u)]
        [TestCase("boiled", 14890u)]
        [TestCase("dumbfound", 15047u)]
        [TestCase("Uganda", 15460u)]
        [TestCase("traded", 15474u)]
        [TestCase("windpipe", 15627u)]
        [TestCase("miscreant", 15752u)]
        [TestCase("Lysol", 15808u)]
        [TestCase("smear", 15853u)]
        [TestCase("legendary", 15870u)]
        [TestCase("miserable", 15987u)]
        [TestCase("romanticist", 16063u)]
        [TestCase("vein", 16185u)]
        [TestCase("barmaid", 16198u)]
        [TestCase("thievery", 16336u)]
        [TestCase("pursing", 16349u)]
        [TestCase("overbalancing", 16362u)]
        [TestCase("athlete", 16443u)]
        [TestCase("severally", 16761u)]
        [TestCase("stack", 16808u)]
        [TestCase("capsule", 17000u)]
        [TestCase("Petty", 17339u)]
        [TestCase("outplayed", 17483u)]
        [TestCase("Shawn", 17506u)]
        [TestCase("spiriting", 17506u)]
        [TestCase("Champlain", 17585u)]
        [TestCase("wisp", 17597u)]
        [TestCase("nun", 17738u)]
        [TestCase("trussing", 17782u)]
        [TestCase("Reilly", 17956u)]
        [TestCase("partaking", 18036u)]
        [TestCase("Dracula", 18372u)]
        [TestCase("Durban", 18608u)]
        [TestCase("tumbler", 18789u)]
        [TestCase("vegetate", 18803u)]
        [TestCase("granule", 19606u)]
        [TestCase("squirm", 19755u)]
        [TestCase("mutest", 19873u)]
        [TestCase("Imogene", 19900u)]
        [TestCase("musty", 20316u)]
        [TestCase("skimpy", 21073u)]
        [TestCase("Thailand", 21209u)]
        [TestCase("work", 21326u)]
        [TestCase("lanker", 21340u)]
        [TestCase("participatory", 21351u)]
        [TestCase("turnaround", 21380u)]
        [TestCase("mostly", 21545u)]
        [TestCase("toaster", 21576u)]
        [TestCase("hirsute", 21735u)]
        [TestCase("tartar", 21760u)]
        [TestCase("knowledgeable", 21835u)]
        [TestCase("Staten", 21864u)]
        [TestCase("mangier", 21969u)]
        [TestCase("super", 22209u)]
        [TestCase("constricted", 22258u)]
        [TestCase("Michelle", 22291u)]
        [TestCase("broadcaster", 22304u)]
        [TestCase("seceded", 22324u)]
        [TestCase("Neva", 22339u)]
        [TestCase("Chernomyrdin", 22350u)]
        [TestCase("doorman", 22400u)]
        [TestCase("repast", 22442u)]
        [TestCase("anal", 22645u)]
        [TestCase("Chelsea", 22658u)]
        [TestCase("Algerian", 22780u)]
        [TestCase("soar", 22952u)]
        [TestCase("endeavoring", 23022u)]
        [TestCase("crochet", 23055u)]
        [TestCase("gravity", 23069u)]
        [TestCase("obsessing", 23282u)]
        [TestCase("astronomic", 23298u)]
        [TestCase("rattletrap", 23356u)]
        [TestCase("bundled", 23412u)]
        [TestCase("embezzled", 23419u)]
        [TestCase("callowest", 23658u)]
        [TestCase("ransacking", 23676u)]
        [TestCase("managing", 23806u)]
        [TestCase("fluently", 23837u)]
        [TestCase("Set", 24153u)]
        [TestCase("melanoma", 24242u)]
        [TestCase("icy", 24398u)]
        [TestCase("bondsmen", 24411u)]
        [TestCase("Miriam", 24563u)]
        [TestCase("interned", 24900u)]
        [TestCase("scientist", 24951u)]
        [TestCase("accessibly", 24963u)]
        [TestCase("detached", 25070u)]
        [TestCase("Beauregard", 25130u)]
        [TestCase("sniping", 25249u)]
        [TestCase("priest", 25274u)]
        [TestCase("gratification", 25361u)]
        [TestCase("Bowen", 25706u)]
        [TestCase("brunch", 25853u)]
        [TestCase("discretionary", 25865u)]
        [TestCase("undemanding", 26233u)]
        [TestCase("announce", 26326u)]
        [TestCase("hail", 26360u)]
        [TestCase("Livy", 26428u)]
        [TestCase("vouched", 26438u)]
        [TestCase("griped", 26539u)]
        [TestCase("untold", 26734u)]
        [TestCase("jauntiest", 26801u)]
        [TestCase("furthering", 26872u)]
        [TestCase("minimization", 26944u)]
        [TestCase("byproduct", 27045u)]
        [TestCase("dynamited", 27289u)]
        [TestCase("misjudged", 27349u)]
        [TestCase("laconically", 27503u)]
        [TestCase("Centigrade", 27958u)]
        [TestCase("Rome", 28224u)]
        [TestCase("portlier", 28227u)]
        [TestCase("buckteeth", 28365u)]
        [TestCase("rededicated", 28452u)]
        [TestCase("echoing", 28459u)]
        [TestCase("enthuse", 28460u)]
        [TestCase("reheating", 28566u)]
        [TestCase("victory", 28859u)]
        [TestCase("massive", 28902u)]
        [TestCase("conquering", 29019u)]
        [TestCase("obituary", 29261u)]
        [TestCase("Purim", 29337u)]
        [TestCase("succeeding", 29435u)]
        [TestCase("Velázquez", 29444u)]
        [TestCase("Browning", 29546u)]
        [TestCase("blitzing", 29567u)]
        [TestCase("originating", 29576u)]
        [TestCase("softened", 29720u)]
        [TestCase("addict", 29730u)]
        [TestCase("non", 30055u)]
        [TestCase("inclined", 30111u)]
        [TestCase("ouster", 30113u)]
        [TestCase("spurn", 30294u)]
        [TestCase("Hokkaido", 30325u)]
        [TestCase("fascinate", 30356u)]
        [TestCase("scientifically", 30456u)]
        [TestCase("symbolize", 30457u)]
        [TestCase("toxemia", 30518u)]
        [TestCase("teased", 30683u)]
        [TestCase("plundered", 30762u)]
        [TestCase("detergent", 30842u)]
        [TestCase("bullion", 30961u)]
        [TestCase("blueberry", 31006u)]
        [TestCase("technological", 31071u)]
        [TestCase("unobtrusively", 31701u)]
        [TestCase("patina", 31778u)]
        [TestCase("preheat", 32060u)]
        [TestCase("entrusting", 32102u)]
        [TestCase("circa", 32142u)]
        [TestCase("kiddo", 32172u)]
        [TestCase("poi", 32187u)]
        [TestCase("disloyal", 32350u)]
        public void TestStringKeyLocation(string key, uint realCrc)
        {
            var request = new RequestKeyWrapper(Encoding.UTF8.GetBytes(key));
            var nodes = _locator.Locate(request);

            var realVBucket = realCrc % VBucketCount;

            // The request's VBucket ID should be set in order for everything to work properly
            Assert.AreEqual(realVBucket, request.VBucket);

            // The output nodes should be the right one
            Assert.AreSame(_nodes[(int)(realVBucket % NodeCount)], nodes.First());
        }
    }
}
