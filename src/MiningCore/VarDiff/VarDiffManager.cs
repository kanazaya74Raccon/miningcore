﻿/* 
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the "Software"), to deal in the Software without restriction, 
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, 
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, 
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial 
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using MiningCore.Configuration;
using MiningCore.Util;
using Contract = MiningCore.Contracts.Contract;

namespace MiningCore.VarDiff
{
    public class VarDiffManager
    {
        public VarDiffManager(VarDiffConfig varDiffOptions)
        {
            options = varDiffOptions;
            
            var variance = varDiffOptions.TargetTime * (varDiffOptions.VariancePercent / 100.0);
            
            /* We need to decided the size of buffer to calculate average. */
            bufferSize = 10; // Last 10 shares is always enough
            
            tMin = varDiffOptions.TargetTime - variance;
            tMax = varDiffOptions.TargetTime + variance;
            
        }

        private readonly int bufferSize;
        private readonly VarDiffConfig options;
        private readonly double tMax;
        private readonly double tMin;

        public double? Update(VarDiffContext ctx, double difficulty, double networkDifficulty, bool onSubmission)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));

            lock (ctx)
            {
                // Get Current Time
                double ts = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
                
                // For the first time, won't change diff.
                if (ctx.LastTs == 0)
                {
                    ctx.LastRtc = ts;
                    ctx.LastTs = ts;
                    ctx.TimeBuffer = new CircularDoubleBuffer(bufferSize);
                    return null;
                } 
                
                var minDiff = options.MinDiff;
                var maxDiff = options.MaxDiff ?? Math.Max(minDiff, networkDifficulty);  // for regtest 

                var sinceLast = ts - ctx.LastTs;
                
                // Always calculate the time until now even there is no share submitted.
                var timeTotal = ctx.TimeBuffer.Sum();
                var timeCount = ctx.TimeBuffer.Size;
                var avg = (timeTotal + sinceLast) / (timeCount + 1);
                
                // Once there is a share submitted, store the time into the buffer and update the last time.
                if (onSubmission)
                {
                    ctx.TimeBuffer.PushBack(sinceLast);
                    ctx.LastTs = ts;
                }
                
                // Check if we need to change the difficulty
                if (ts - ctx.LastRtc < options.RetargetTime || avg >= tMin && avg <= tMax)
                    return null;
                                
                // Possible New Diff
                var newDiff = difficulty * options.TargetTime / avg;
                if (newDiff < minDiff)
                    newDiff = minDiff;
                if (newDiff > maxDiff)
                    newDiff = maxDiff;
                
                // RTC if the Diff is changed
                if (newDiff != difficulty)
                {
                    ctx.LastRtc = ts;

                    // Due to change of diff, Buffer needs to be cleared
                    ctx.TimeBuffer = new CircularDoubleBuffer(bufferSize);
                    return newDiff;
                }
            }
            
            return null;
        }
        
        public double? Update(VarDiffContext ctx, double difficulty, double networkDifficulty)
        {
            return Update(ctx, difficulty, networkDifficulty, false);
        }
        
    }
}
