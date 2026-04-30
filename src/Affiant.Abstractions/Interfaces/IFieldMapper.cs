namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IFieldMapper<T>
{
    T MapFromAffidavit(Affidavit affidavit);
    Affidavit MapToAffidavit(T entity, string operationType);
}
