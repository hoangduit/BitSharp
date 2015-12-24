﻿using BitSharp.Core.Domain;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace BitSharp.Core.Builders
{
    internal static class BlockValidator
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static async Task ValidateBlockAsync(ICoreStorage coreStorage, IBlockchainRules rules, ChainedHeader chainedHeader, ISourceBlock<ValidatableTx> validatableTxes, CancellationToken cancelToken = default(CancellationToken))
        {
            // validate merkle root
            var merkleStream = new MerkleStream();
            var merkleValidator = InitMerkleValidator(chainedHeader, merkleStream, cancelToken);

            // begin feeding the merkle validator
            validatableTxes.LinkTo(merkleValidator, new DataflowLinkOptions { PropagateCompletion = true });

            // validate transactions
            var txValidator = InitTxValidator(rules, chainedHeader, cancelToken);

            // begin feeding the tx validator
            merkleValidator.LinkTo(txValidator, new DataflowLinkOptions { PropagateCompletion = true });

            // validate scripts
            var scriptValidator = InitScriptValidator(rules, chainedHeader, cancelToken);

            // begin feeding the script validator
            txValidator.LinkTo(scriptValidator, new DataflowLinkOptions { PropagateCompletion = true });

            await merkleValidator.Completion;
            await txValidator.Completion;
            await scriptValidator.Completion;

            if (!rules.BypassPrevTxLoading)
            {
                try
                {
                    merkleStream.FinishPairing();
                }
                //TODO
                catch (InvalidOperationException)
                {
                    throw CreateMerkleRootException(chainedHeader);
                }
                if (merkleStream.RootNode.Hash != chainedHeader.MerkleRoot)
                    throw CreateMerkleRootException(chainedHeader);
            }
        }

        private static TransformBlock<ValidatableTx, ValidatableTx> InitMerkleValidator(ChainedHeader chainedHeader, MerkleStream merkleStream, CancellationToken cancelToken)
        {
            return new TransformBlock<ValidatableTx, ValidatableTx>(
                validatableTx =>
                {
                    try
                    {
                        merkleStream.AddNode(new MerkleTreeNode(validatableTx.TxIndex, 0, validatableTx.Transaction.Hash, false));
                    }
                    //TODO
                    catch (InvalidOperationException)
                    {
                        throw CreateMerkleRootException(chainedHeader);
                    }
                    return validatableTx;
                },
                new ExecutionDataflowBlockOptions { CancellationToken = cancelToken });
        }

        private static TransformManyBlock<ValidatableTx, Tuple<ValidatableTx, int>> InitTxValidator(IBlockchainRules rules, ChainedHeader chainedHeader, CancellationToken cancelToken)
        {
            return new TransformManyBlock<ValidatableTx, Tuple<ValidatableTx, int>>(
                validatableTx =>
                {
                    rules.ValidateTransaction(chainedHeader, validatableTx);

                    if (!rules.IgnoreScripts && !validatableTx.IsCoinbase)
                    {
                        var scripts = new Tuple<ValidatableTx, int>[validatableTx.Transaction.Inputs.Length];
                        for (var i = 0; i < validatableTx.Transaction.Inputs.Length; i++)
                            scripts[i] = Tuple.Create(validatableTx, i);

                        return scripts;
                    }
                    else
                        return new Tuple<ValidatableTx, int>[0];
                },
                new ExecutionDataflowBlockOptions { CancellationToken = cancelToken, MaxDegreeOfParallelism = Environment.ProcessorCount });
        }

        private static ActionBlock<Tuple<ValidatableTx, int>> InitScriptValidator(IBlockchainRules rules, ChainedHeader chainedHeader, CancellationToken cancelToken)
        {
            return new ActionBlock<Tuple<ValidatableTx, int>>(
                tuple =>
                {
                    var validatableTx = tuple.Item1;
                    var inputIndex = tuple.Item2;
                    var txInput = validatableTx.Transaction.Inputs[inputIndex];
                    var prevTxOutputs = validatableTx.PrevTxOutputs[inputIndex];

                    if (!rules.IgnoreScriptErrors)
                    {
                        rules.ValidationTransactionScript(chainedHeader, validatableTx.Transaction, validatableTx.TxIndex, txInput, inputIndex, prevTxOutputs);
                    }
                    else
                    {
                        try
                        {
                            rules.ValidationTransactionScript(chainedHeader, validatableTx.Transaction, validatableTx.TxIndex, txInput, inputIndex, prevTxOutputs);
                        }
                        catch (Exception ex)
                        {
                            var aggEx = ex as AggregateException;
                            logger.Debug($"Ignoring script errors in block: {chainedHeader.Height,9:N0}, errors: {(aggEx?.InnerExceptions.Count ?? -1):N0}");
                        }
                    }
                },
                new ExecutionDataflowBlockOptions { CancellationToken = cancelToken, MaxDegreeOfParallelism = Environment.ProcessorCount });
        }

        private static ValidationException CreateMerkleRootException(ChainedHeader chainedHeader)
        {
            return new ValidationException(chainedHeader.Hash, $"Failing block {chainedHeader.Hash} at height {chainedHeader.Height}: Merkle root is invalid");
        }
    }
}
