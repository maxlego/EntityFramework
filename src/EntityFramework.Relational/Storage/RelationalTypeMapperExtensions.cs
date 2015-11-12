// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using JetBrains.Annotations;

namespace Microsoft.Data.Entity.Storage
{
    public static class RelationalTypeMapperExtensions
    {
        public static RelationalTypeMapping GetMappingForValue(
            [CanBeNull] this IRelationalTypeMapper typeMapper,
            [CanBeNull] object value)
            => (value == null)
               || (value == DBNull.Value)
               || (typeMapper == null)
                ? RelationalTypeMapping.NullMapping
                : typeMapper.GetMapping(value.GetType());
    }
}
