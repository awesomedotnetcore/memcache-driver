﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Criteo.Memcache.Node;

namespace Criteo.Memcache.Locator
{
    internal class RoundRobinLocator : INodeLocator
    {
        private IList<IMemcacheNode> _nodes;

        public void Initialize(IList<IMemcacheNode> nodes)
        {
            _nodes = nodes;
        }

        private int _lastPosition = 0;
        public IMemcacheNode Locate(string key)
        {
            for(int i=0; i<_nodes.Count; ++i)
            {
                var position = Interlocked.Increment(ref _lastPosition) % _nodes.Count;
                position = position >= 0 ? position : position + _nodes.Count;
                var selectedNode = _nodes[position];
                if (!selectedNode.IsDead)
                    return selectedNode;
            }

            return null;
        }
    }
}