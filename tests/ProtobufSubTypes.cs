/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using NUnit.Framework;
using ProtoBuf.Meta;
using QuantConnect.Data;
using QuantConnect.DataSource;

namespace QuantConnect.DataLibrary.Tests
{
    /// <summary>
    /// Registers this repository's data types with protobuf, which is what LEAN's live data
    /// distributor does before it serializes a point.
    ///
    /// It happens here, once, rather than in each round trip test: the model freezes the moment a
    /// serializer is generated for <see cref="BaseData"/>, so the first test to serialize anything
    /// would leave every later registration throwing. The field numbers are this repository's to
    /// choose and have to stay clear of each other and of the ones BaseData gives its own
    /// subclasses, which reach 400.
    /// </summary>
    [SetUpFixture]
    public class ProtobufSubTypes
    {
        [OneTimeSetUp]
        public void RegisterSubTypes()
        {
            RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(2000, typeof(SECReport8K));
            RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(2001, typeof(SEC13F));
        }
    }
}
